# Runbook de despliegue en AWS — 8-Ball Pool Manager API

Objetivo: de cuenta vacía a API funcionando en ~30 minutos.
Región: `us-east-1`. Todo por AWS CLI salvo dos pasos de consola web.

---

## Antes de arrancar: cambios de código ya aplicados

Estos tres bugs habrían hecho fallar el despliegue. Ya están corregidos en el repo:

| Archivo | Problema | Por qué importaba |
|---|---|---|
| `Services/S3Service.cs` | `new AmazonS3Client(null, null, config)` | En AWS no hay `S3:AccessKey`/`SecretKey` (se usa el task role). El constructor lanzaba `ArgumentNullException` y el contenedor moría **antes** de responder el health check → ECS lo mataba en loop y nunca veías el error real. |
| `Services/S3Service.cs` | URL de descarga firmada a 24 h | Las credenciales del task role son temporales. La URL deja de funcionar cuando expira el session token (~6 h) aunque la firma diga 24 h. Bajado a 1 h. |
| `.github/workflows/ci.yml` | Solo pusheaba la tag `$GITHUB_SHA` | El servicio de ECS apunta a un tag fijo. `--force-new-deployment` volvía a bajar **la misma imagen vieja**. Ahora pushea `latest` + SHA. |

**Commiteá esto antes de empezar** (todavía sin push, o el pipeline va a fallar porque ECR no existe):

```bash
git add Services/S3Service.cs .github/workflows/ci.yml
git commit -m "fix: credenciales S3 via task role + pipeline redeploy real"
```

---

## Veredicto sobre "Posible estructura de 8-ball.txt"

**El diseño es correcto.** ECS Express + ECR + S3 + GitHub Actions sigue siendo la mejor opción hoy — App Runner entró en maintenance mode y deja de aceptar clientes nuevos el 30/04/2026, y AWS señala explícitamente a ECS Express como su reemplazo.

Se mantiene igual:

- ECS Express Mode para la API (Fargate + ALB + HTTPS + autoscaling, sin configurar nada)
- ECR para las imágenes
- Aurora PostgreSQL Express (fuera de VPC, IAM auth) — GA desde 25/03/2026, entra en free tier
- S3 para fotos
- EC2 para Keycloak

Se corrige:

1. **`KeycloakUrlRewriteHandler` no se usa más.** Existía porque `Authority` (IP privada) y `PublicAuthority` (Elastic IP) diferían y había que reescribir el discovery document. Solución: usar **la Elastic IP en los dos lados**. Si `Authority == PublicAuthority`, el handler ni se instancia (mirá la condición en `Program.cs:60`). Menos piezas móviles, menos superficie de falla.
2. **Keycloak sin Postgres aparte.** `start-dev` con la base H2 sobre un volumen Docker persistido en el EBS de la instancia. Un contenedor en vez de dos, y no hay que sincronizar credenciales.
3. **No hay que hacer `GRANT rds_iam` a mano.** Con Aurora Express el internet access gateway ya deja al usuario master (`postgres`) configurado con `rds_iam` automáticamente. Ese paso manual desaparece.
4. **`GSS Encryption Mode=Disable` desde el minuto cero**, como bien anotaste. Va en la connection string de la Fase 7.

Sobre el 504 anterior: coincido en que el síntoma es del driver, no de la infra. Pero ojo — el crash de `S3Service` en el arranque produce exactamente el mismo cuadro clínico (tarea que no levanta, ALB devolviendo 5xx), así que probablemente tenías **dos** bugs superpuestos y arreglar uno solo no te iba a mostrar mejora.

---

## Orden de ejecución

Aurora tarda un poco en quedar `available` y el build de .NET tarda varios minutos. Por eso **se lanzan primero y se trabaja en paralelo** mientras provisionan.

```
Fase 0  Credenciales                    3 min
Fase 1  Lanzar Aurora        ──┐        1 min  (sigue en background)
Fase 2  ECR + build + push   ──┤        6 min  (corre mientras Aurora provisiona)
Fase 3  Bucket S3              │        1 min
Fase 4  Keycloak en EC2      ──┘        6 min
Fase 5  Roles IAM de ECS                2 min
Fase 6  Migraciones                     2 min
Fase 7  Crear servicio ECS Express      4 min
Fase 8  Configurar realm de Keycloak    5 min
Fase 9  Secrets de GitHub Actions       2 min
```

---

## Fase 0 — Credenciales (3 min)

Consola web → IAM → Users → **Create user** → `poolmanager-admin` → Attach policies directly → `AdministratorAccess`.
Después: pestaña **Security credentials** → Create access key → *Command Line Interface*.

> Es un usuario de setup, no de producción. Cuando termines podés borrarlo y quedarte solo con `poolmanager-deployer` (Fase 9), que tiene permisos mínimos.

```bash
aws configure
# AWS Access Key ID:     <la que acabás de crear>
# AWS Secret Access Key: <...>
# Default region name:   us-east-1
# Default output format: json
```

Guardá el account ID en una variable, se usa en casi todas las fases:

```bash
export ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
export AWS_REGION=us-east-1
echo $ACCOUNT_ID
```

> **En PowerShell** usá `$env:ACCOUNT_ID = (aws sts get-caller-identity --query Account --output text)` y `$env:VAR` para leerlas. El resto de los comandos son idénticos salvo el salto de línea: reemplazá `\` por backtick `` ` ``.

**Checkpoint:** `aws sts get-caller-identity` devuelve tu ARN.

---

## Fase 1 — Lanzar Aurora (1 min, sigue en background)

```bash
aws rds create-db-cluster \
  --db-cluster-identifier poolmanager-db \
  --engine aurora-postgresql \
  --with-express-configuration
```

Eso es todo. Un solo flag crea cluster + instancia serverless + internet access gateway + IAM auth para el usuario master. Sin VPC, sin subnet group, sin security group.

Defaults que te da: usuario master `postgres`, base `postgres`, puerto 5432, encriptado, backup 1 día.

**No esperes a que termine.** Seguí con la Fase 2 y volvés a chequear más tarde.

---

## Fase 2 — ECR + primera imagen (6 min)

```bash
aws ecr create-repository --repository-name poolmanager-api

aws ecr get-login-password --region us-east-1 \
  | docker login --username AWS --password-stdin $ACCOUNT_ID.dkr.ecr.us-east-1.amazonaws.com

export ECR=$ACCOUNT_ID.dkr.ecr.us-east-1.amazonaws.com/poolmanager-api

docker build -t $ECR:latest .
docker push $ECR:latest
```

> El `docker build` de .NET 10 tarda 3-5 min la primera vez. Dejalo corriendo y arrancá la Fase 3 en otra terminal.

**Checkpoint:** `aws ecr list-images --repository-name poolmanager-api` muestra la tag `latest`.

---

## Fase 3 — Bucket S3 (1 min)

```bash
export BUCKET=poolmanager-profile-pictures-$ACCOUNT_ID

aws s3api create-bucket --bucket $BUCKET --region us-east-1
```

> Se le agrega el account ID porque los nombres de bucket son globales y `poolmanager-profile-pictures` a secas puede estar tomado.

El bucket queda privado. No hace falta abrirlo: la API sirve todo por URLs pre-firmadas.

---

## Fase 4 — Keycloak en EC2 (6 min)

### 4.1 Elastic IP primero

El orden importa: necesitás la IP **antes** de lanzar la instancia, porque va incrustada en la config de Keycloak.

```bash
export EIP_ALLOC=$(aws ec2 allocate-address --domain vpc --query AllocationId --output text)
export KC_IP=$(aws ec2 describe-addresses --allocation-ids $EIP_ALLOC \
  --query 'Addresses[0].PublicIp' --output text)
echo "Keycloak va a vivir en: $KC_IP"
```

### 4.2 Security group

```bash
export VPC_ID=$(aws ec2 describe-vpcs --filters Name=is-default,Values=true \
  --query 'Vpcs[0].VpcId' --output text)

export KC_SG=$(aws ec2 create-security-group \
  --group-name poolmanager-keycloak-sg \
  --description "Keycloak 8080" \
  --vpc-id $VPC_ID --query GroupId --output text)

# 8080 abierto a internet: las tareas de Fargate tienen IP pública dinámica,
# no se puede restringir a un rango fijo sin meterse con NAT/VPC endpoints.
aws ec2 authorize-security-group-ingress --group-id $KC_SG \
  --protocol tcp --port 8080 --cidr 0.0.0.0/0
```

### 4.3 Lanzar la instancia

```bash
export MI_IP=$(curl -s https://checkip.amazonaws.com)
aws ec2 authorize-security-group-ingress --group-id $KC_SG \
  --protocol tcp --port 22 --cidr $MI_IP/32

cat > /tmp/kc-userdata.sh <<EOF
#!/bin/bash
dnf install -y docker
systemctl enable --now docker
docker volume create kcdata
docker run -d --name keycloak --restart always -p 8080:8080 \\
  -e KC_BOOTSTRAP_ADMIN_USERNAME=admin \\
  -e KC_BOOTSTRAP_ADMIN_PASSWORD='CambiameYa_2026!' \\
  -e KEYCLOAK_ADMIN=admin \\
  -e KEYCLOAK_ADMIN_PASSWORD='CambiameYa_2026!' \\
  -e KC_HOSTNAME=http://$KC_IP:8080 \\
  -e KC_HOSTNAME_STRICT=false \\
  -e KC_HTTP_ENABLED=true \\
  -v kcdata:/opt/keycloak/data \\
  quay.io/keycloak/keycloak:26.4 start-dev
EOF

export AMI=$(aws ssm get-parameter \
  --name /aws/service/ami-amazon-linux-latest/al2023-ami-kernel-default-x86_64 \
  --query 'Parameter.Value' --output text)

export KC_INSTANCE=$(aws ec2 run-instances \
  --image-id $AMI \
  --instance-type t3.small \
  --security-group-ids $KC_SG \
  --user-data file:///tmp/kc-userdata.sh \
  --tag-specifications 'ResourceType=instance,Tags=[{Key=Name,Value=poolmanager-keycloak}]' \
  --query 'Instances[0].InstanceId' --output text)

aws ec2 wait instance-running --instance-ids $KC_INSTANCE
aws ec2 associate-address --instance-id $KC_INSTANCE --allocation-id $EIP_ALLOC
```

> `t3.small` (~$15/mes). `t3.micro` entra en free tier y funciona, pero con 1 GB Keycloak arranca muy justo y puede tardar el doble. Si estás optimizando costo y no tiempo, cambiá el tipo.

**Checkpoint** (dale ~2 min para que instale Docker y baje la imagen):

```bash
curl -s http://$KC_IP:8080/realms/master/.well-known/openid-configuration | head -c 200
```

Tiene que devolver JSON con `"issuer":"http://<KC_IP>:8080/realms/master"`. Si devuelve vacío, esperá un minuto más.

---

## Fase 5 — Roles IAM de ECS (2 min)

Dos roles obligatorios de Express Mode, más un tercero para tu aplicación.

```bash
# Task execution role — ECS lo usa para bajar la imagen y escribir logs
aws iam create-role --role-name ecsTaskExecutionRole \
  --assume-role-policy-document '{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"ecs-tasks.amazonaws.com"},"Action":"sts:AssumeRole"}]}'
aws iam attach-role-policy --role-name ecsTaskExecutionRole \
  --policy-arn arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy

# Infrastructure role — ECS lo usa para crear el ALB, target groups, autoscaling
aws iam create-role --role-name ecsInfrastructureRoleForExpressServices \
  --assume-role-policy-document '{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"ecs.amazonaws.com"},"Action":"sts:AssumeRole"}]}'
aws iam attach-role-policy --role-name ecsInfrastructureRoleForExpressServices \
  --policy-arn arn:aws:iam::aws:policy/service-role/AmazonECSInfrastructureRoleforExpressGatewayServices

# Task role — lo usa TU CÓDIGO para hablar con RDS y S3
aws iam create-role --role-name poolmanager-task-role \
  --assume-role-policy-document '{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"ecs-tasks.amazonaws.com"},"Action":"sts:AssumeRole"}]}'
```

Ahora el permiso a la base. Necesita el **resource ID del cluster**, no el nombre:

```bash
export DB_RESOURCE_ID=$(aws rds describe-db-clusters --db-cluster-identifier poolmanager-db \
  --query 'DBClusters[0].DbClusterResourceId' --output text)
export DB_HOST=$(aws rds describe-db-clusters --db-cluster-identifier poolmanager-db \
  --query 'DBClusters[0].Endpoint' --output text)
echo "resource id: $DB_RESOURCE_ID"
echo "endpoint:    $DB_HOST"
```

> Si estos comandos fallan o devuelven vacío, Aurora todavía está creándose. Chequeá con
> `aws rds describe-db-clusters --db-cluster-identifier poolmanager-db --query 'DBClusters[0].Status'`
> hasta que diga `available`.

```bash
cat > /tmp/task-policy.json <<EOF
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "AuroraIamAuth",
      "Effect": "Allow",
      "Action": "rds-db:connect",
      "Resource": "arn:aws:rds-db:us-east-1:$ACCOUNT_ID:dbuser:$DB_RESOURCE_ID/postgres"
    },
    {
      "Sid": "S3Objects",
      "Effect": "Allow",
      "Action": ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"],
      "Resource": "arn:aws:s3:::$BUCKET/*"
    },
    {
      "Sid": "S3Bucket",
      "Effect": "Allow",
      "Action": ["s3:ListBucket", "s3:GetBucketLocation"],
      "Resource": "arn:aws:s3:::$BUCKET"
    }
  ]
}
EOF

aws iam put-role-policy --role-name poolmanager-task-role \
  --policy-name poolmanager-rds-s3 \
  --policy-document file:///tmp/task-policy.json
```

> **Trampa clásica:** el ARN de `rds-db:connect` lleva el `DbClusterResourceId` (`cluster-XXXXXX`), no `poolmanager-db`. Si ponés el nombre, la conexión falla con un error de autenticación que no menciona IAM por ningún lado.

---

## Fase 6 — Migraciones (2 min)

La forma más rápida y sin instalar nada: **CloudShell**, que ya viene con `psql` y credenciales.

Consola → ícono de CloudShell (arriba a la derecha) → pegá:

```bash
export DB_HOST=$(aws rds describe-db-clusters --db-cluster-identifier poolmanager-db \
  --query 'DBClusters[0].Endpoint' --output text)
export PGPASSWORD=$(aws rds generate-db-auth-token \
  --hostname $DB_HOST --port 5432 --username postgres --region us-east-1)
psql "host=$DB_HOST port=5432 dbname=postgres user=postgres sslmode=require"
```

Cuando veas el prompt `postgres=>`, pegá el contenido de `migrate.sql` y dale Enter.

Verificá:

```sql
\dt
```

Tenés que ver `Players`, `Matches` y `__EFMigrationsHistory`.

> Si preferís hacerlo local y tenés `psql` instalado, son los mismos tres comandos. El token vale 15 minutos; si tardás más, regeneralo.

---

## Fase 7 — Crear el servicio ECS Express (4 min)

Acá se juntan todas las piezas. Revisá que tengas las variables cargadas:

```bash
echo "$ACCOUNT_ID / $ECR / $BUCKET / $KC_IP / $DB_HOST"
```

```bash
cat > /tmp/container.json <<EOF
{
  "image": "$ECR:latest",
  "containerPort": 8080,
  "environment": [
    {"name": "AWS__UseIamAuth", "value": "true"},
    {"name": "ConnectionStrings__DefaultConnection",
     "value": "Host=$DB_HOST;Port=5432;Database=postgres;Username=postgres;SSL Mode=Require;Trust Server Certificate=true;GSS Encryption Mode=Disable"},
    {"name": "Keycloak__Authority", "value": "http://$KC_IP:8080/realms/poolmanager"},
    {"name": "Keycloak__ClientId", "value": "poolmanager-api"},
    {"name": "S3__ServiceUrl", "value": ""},
    {"name": "S3__AccessKey", "value": ""},
    {"name": "S3__SecretKey", "value": ""},
    {"name": "S3__Region", "value": "us-east-1"},
    {"name": "S3__BucketName", "value": "$BUCKET"},
    {"name": "S3__ForcePathStyle", "value": "false"},
    {"name": "ASPNETCORE_ENVIRONMENT", "value": "Production"}
  ]
}
EOF

aws ecs create-express-gateway-service \
  --service-name poolmanager-api \
  --primary-container file:///tmp/container.json \
  --execution-role-arn arn:aws:iam::$ACCOUNT_ID:role/ecsTaskExecutionRole \
  --infrastructure-role-arn arn:aws:iam::$ACCOUNT_ID:role/ecsInfrastructureRoleForExpressServices \
  --task-role-arn arn:aws:iam::$ACCOUNT_ID:role/poolmanager-task-role \
  --cpu 512 --memory 1024 \
  --health-check-path /health \
  --scaling-target '{"minTaskCount":1,"maxTaskCount":3}' \
  --monitor-resources
```

### Por qué cada variable

- **`AWS__UseIamAuth=true`** activa la rama de `Program.cs` que genera tokens IAM con `RDSAuthTokenGenerator` en vez de usar password. Sin esto intenta conectarse con la password de `appsettings.json` (que no existe en Aurora Express) y falla.
- **`GSS Encryption Mode=Disable`** — tu hallazgo. Npgsql 10 negocia GSSAPI por defecto (`Prefer`); el gateway de Aurora no lo soporta y la conexión se cuelga hasta timeout. De ahí el 504.
- **`SSL Mode=Require`** es obligatorio: el internet access gateway solo acepta TLS.
- **`S3__ServiceUrl=""`** vacío hace que `S3Service` caiga en la rama de `RegionEndpoint` en vez de apuntar a MinIO.
- **`S3__AccessKey=""` y `S3__SecretKey=""`** son imprescindibles y fáciles de pasar por alto: `appsettings.json` trae `minioadmin` / `minio_secret_123` hardcodeados y viajan dentro de la imagen. Si no los pisás con vacío, la API los toma como válidos, ignora el task role y firma todas las URLs con credenciales de MinIO → `SignatureDoesNotMatch` en cada llamada a `/storage/*`. Vaciarlos fuerza la cadena de credenciales por defecto.
- **No se define `Keycloak__ClientSecret`** — la API solo *valida* tokens, no los emite. No lo necesita.
- **No se define `Keycloak__PublicAuthority`** — por eso el `KeycloakUrlRewriteHandler` no se activa.

Si `--monitor-resources` corta con `Unable to assume the service linked role`, esperá un minuto (los roles IAM son *eventually consistent*) y reintentá el mismo comando.

Guardá la URL:

```bash
export SERVICE_ARN=$(aws ecs describe-express-gateway-service \
  --service-arn arn:aws:ecs:us-east-1:$ACCOUNT_ID:service/default/poolmanager-api \
  --query 'service.serviceArn' --output text)

aws ecs describe-express-gateway-service --service-arn $SERVICE_ARN \
  --query 'service.activeConfigurations[0].ingressPaths[*].endpoint' --output text
```

**Checkpoint:**

```bash
curl https://<tu-url>.ecs.us-east-1.on.aws/health
```

Tiene que devolver `Healthy`.

### Si no levanta

```bash
aws logs tail /ecs/poolmanager-api --follow
```

| Lo que ves en el log | Qué es |
|---|---|
| `Npgsql...timeout` / se cuelga en el arranque | Falta `GSS Encryption Mode=Disable` |
| `PostgresException: PAM authentication failed` | El ARN de `rds-db:connect` está mal (usaste el nombre en vez del resource ID) |
| `ArgumentNullException` en `S3Service` | No aplicaste el fix de la Fase 0 |
| `SignatureDoesNotMatch` al firmar URLs | El task role no tiene los permisos de S3, o el bucket del env var no coincide |
| Tarea que arranca y muere en loop sin log claro | Casi siempre es una excepción en el `await s3.EnsureBucketExists()` del arranque de `Program.cs` |

---

## Fase 8 — Configurar el realm de Keycloak (5 min)

Navegador → `http://<KC_IP>:8080` → admin / `CambiameYa_2026!`

1. **Create realm** → nombre exacto: `poolmanager`
2. **Clients → Create client**
   - Client ID: `poolmanager-api`
   - Client authentication: **ON**
   - Authentication flow: marcá **Standard flow** y **Direct access grants**
3. **Client scopes** → `poolmanager-api-dedicated` → **Add mapper → By configuration → Audience**
   - Included Client Audience: `poolmanager-api`
   - Add to access token: **ON**

   > Sin este mapper el token no lleva `aud` y tu API lo rechaza, porque `Program.cs` tiene `ValidateAudience = true`. Es el error más común de este setup.
4. **Realm roles → Create role** → `admin`
5. **Users → Add user** → asignale el rol en la pestaña **Role mapping**

Probá el token:

```bash
curl -X POST "http://$KC_IP:8080/realms/poolmanager/protocol/openid-connect/token" \
  -d "client_id=poolmanager-api" \
  -d "client_secret=<el secret de la pestaña Credentials>" \
  -d "username=<tu usuario>" \
  -d "password=<tu password>" \
  -d "grant_type=password"
```

Pegá el `access_token` en <https://jwt.io> y confirmá dos cosas: `"iss": "http://<KC_IP>:8080/realms/poolmanager"` y que `aud` incluya `poolmanager-api`.

Después probá contra la API real:

```bash
curl https://<tu-url>.ecs.us-east-1.on.aws/players/me \
  -H "Authorization: Bearer <access_token>"
```

---

## Fase 9 — GitHub Actions (2 min)

Usuario dedicado con permisos mínimos, no el admin de la Fase 0:

```bash
aws iam create-user --user-name poolmanager-deployer

cat > /tmp/deployer-policy.json <<EOF
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "ecr:GetAuthorizationToken",
        "ecr:BatchCheckLayerAvailability",
        "ecr:InitiateLayerUpload",
        "ecr:UploadLayerPart",
        "ecr:CompleteLayerUpload",
        "ecr:PutImage",
        "ecr:BatchGetImage"
      ],
      "Resource": "*"
    },
    {
      "Effect": "Allow",
      "Action": ["ecs:UpdateService", "ecs:DescribeServices"],
      "Resource": "arn:aws:ecs:us-east-1:$ACCOUNT_ID:service/default/poolmanager-api"
    }
  ]
}
EOF

aws iam put-user-policy --user-name poolmanager-deployer \
  --policy-name poolmanager-deploy --policy-document file:///tmp/deployer-policy.json

aws iam create-access-key --user-name poolmanager-deployer
```

GitHub → repo → Settings → Secrets and variables → Actions → **New repository secret**:

- `AWS_ACCESS_KEY_ID`
- `AWS_SECRET_ACCESS_KEY`

Y ahora sí:

```bash
git push origin main
```

El pipeline corre tests → buildea → pushea `latest` + SHA a ECR → fuerza el redeploy → espera a que estabilice.

---

## Verificación final

```bash
# 1. Health check
curl https://<url>/health

# 2. Swagger abre
open https://<url>/swagger

# 3. Endpoint protegido devuelve 401 sin token
curl -i https://<url>/players

# 4. Con token devuelve 200
curl https://<url>/players/me -H "Authorization: Bearer <token>"

# 5. La base responde (no es solo el health check)
curl https://<url>/matches -H "Authorization: Bearer <token>"

# 6. S3 firma URLs
curl "https://<url>/storage/upload-url?fileName=test.jpg" -H "Authorization: Bearer <token>"
```

Si los seis pasan, está terminado.

---

## Costo estimado

| Servicio | Aprox. mensual |
|---|---|
| ECS Express (Fargate 0.5 vCPU / 1 GB, 1 tarea) | ~$18 |
| Application Load Balancer | ~$17 |
| EC2 t3.small (Keycloak) | ~$15 |
| Elastic IP (asociada) | $0 |
| Aurora Serverless v2 (mínimo, free tier el 1er año) | $0–15 |
| S3 + ECR + CloudWatch | ~$2 |
| **Total** | **~$52–67/mes** |

Bajarlo: `t3.micro` para Keycloak (−$8) y `--cpu 256 --memory 512` en ECS (−$9). El ALB es el piso duro — es el costo fijo de tener HTTPS con dominio propio.

---

## Cuando termines: limpiar

```bash
aws ecs delete-express-gateway-service --service-arn $SERVICE_ARN --monitor-resources
aws ec2 terminate-instances --instance-ids $KC_INSTANCE
aws ec2 release-address --allocation-id $EIP_ALLOC
aws rds delete-db-cluster --db-cluster-identifier poolmanager-db --skip-final-snapshot
aws s3 rb s3://$BUCKET --force
aws ecr delete-repository --repository-name poolmanager-api --force
aws iam delete-user --user-name poolmanager-admin   # borrá antes sus access keys
```

Anotá `$SERVICE_ARN`, `$KC_INSTANCE` y `$EIP_ALLOC` en algún lado: si perdés la terminal, la Elastic IP sin asociar sigue cobrándose y es lo que más se olvida.

---

## Fuentes

- [Amazon ECS Express Mode](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/express-service-overview.html)
- [Create your first Express Mode service using the AWS CLI](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/express-service-getting-started.html)
- [CreateExpressGatewayService API](https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_CreateExpressGatewayService.html)
- [Aurora PostgreSQL — Create with express configuration](https://docs.aws.amazon.com/AmazonRDS/latest/AuroraUserGuide/CHAP_GettingStartedAurora.AuroraPostgreSQL.ExpressConfig.html)
- [Conectarse a Aurora con IAM desde la línea de comandos](https://docs.aws.amazon.com/AmazonRDS/latest/AuroraUserGuide/UsingWithRDS.IAMDBAuth.Connecting.AWSCLI.PostgreSQL.html)
- [Npgsql — Security and Encryption (GSS Encryption Mode)](https://www.npgsql.org/doc/security.html)
- [Keycloak — Configuring the hostname (v2)](https://www.keycloak.org/server/hostname)
- [AWS App Runner](https://aws.amazon.com/apprunner/)
