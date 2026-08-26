# Deploy AWS — 8-Ball Pool Manager API (solo consola web)

Región: **us-east-1** en todos los pasos. Sin AWS CLI local.
La imagen Docker la buildea GitHub Actions, no tu máquina.

---

## Antes: 4 cambios de código ya aplicados

| Archivo | Cambio |
|---|---|
| `Services/S3Service.cs` | Usa el task role cuando no hay AccessKey/SecretKey. Antes crasheaba al arrancar. |
| `Services/S3Service.cs` | URL de descarga: 24 h → 1 h. |
| `appsettings.json` | Sacadas las claves de MinIO. Ahora viven en `docker-compose.yml`. |
| `.github/workflows/ci.yml` | Pushea tag `latest` además del SHA. Sin esto el redeploy bajaba la imagen vieja. |

Local sigue funcionando igual (`docker compose up -d`).

Commiteá, **sin pushear**:

```powershell
git add Services/S3Service.cs appsettings.json docker-compose.yml .github/workflows/ci.yml DEPLOY-AWS.md
git commit -m "fix: credenciales S3 via task role + pipeline redeploy real"
```

---

## Orden

```
1  Usuario IAM para GitHub          3 min
2  ECR                              1 min
3  Push a GitHub → imagen en ECR    6 min   ← corre solo, seguí con 4
4  Aurora                           2 min
5  S3                               1 min
6  Elastic IP + EC2 Keycloak        6 min
7  Roles IAM                        4 min
8  Migraciones (CloudShell)         2 min
9  Servicio ECS Express             4 min
10 Realm de Keycloak                5 min
```

Anotá a medida que avanzás:

```
ACCOUNT_ID  = ____________
KC_IP       = ____________
DB_HOST     = ____________
DB_RES_ID   = ____________
BUCKET      = ____________
API_URL     = ____________
```

Tu **ACCOUNT_ID** está arriba a la derecha, al clickear tu nombre de usuario.

---

## 1. Usuario IAM para GitHub Actions

IAM → Users → **Create user**

- Nombre: `poolmanager-deployer`
- **NO** marcar acceso a consola
- Permissions → *Attach policies directly* → marcá:
  - `AmazonEC2ContainerRegistryPowerUser`
  - `AmazonECS_FullAccess`
- Create user

Entrá al usuario → **Security credentials** → Create access key → *Third-party service* → confirmá → **Create**.

Dejá la pantalla abierta, los valores se usan en el paso 3.

---

## 2. ECR

ECR → Repositories → **Create repository**

- Nombre: `poolmanager-api`
- Resto por defecto → Create

---

## 3. Push a GitHub

GitHub → tu repo → Settings → Secrets and variables → **Actions** → New repository secret. Creá dos:

| Name | Value |
|---|---|
| `AWS_ACCESS_KEY_ID` | del paso 1 |
| `AWS_SECRET_ACCESS_KEY` | del paso 1 |

Después cerrá la pestaña de AWS con las claves.

```powershell
git push origin main
```

En la pestaña **Actions** vas a ver:

- `build-and-test` ✅
- `deploy` → pushea la imagen ✅ y **falla en "Force new ECS deployment"** ❌

Ese fallo es esperado: el servicio ECS todavía no existe. Se crea en el paso 9 y a partir de ahí el pipeline queda verde.

**Verificá:** ECR → `poolmanager-api` → tiene que haber una imagen con tag `latest`.

Mientras buildea, seguí con el paso 4.

---

## 4. Aurora

RDS → Databases. En la pantalla de bienvenida, sección **Create with express configuration in seconds** → **Create**.

- DB cluster identifier: `poolmanager-db`
- Resto por defecto → **Create database**

Queda `Available` en segundos.

Entrá al cluster → **Connectivity & security** → anotá el **Writer endpoint** → ese es tu `DB_HOST`.

Pestaña **Configuration** → anotá el **Resource ID** (empieza con `cluster-`) → ese es tu `DB_RES_ID`.

> Usuario master: `postgres`. Base: `postgres`. Auth: solo IAM (ya viene configurada).

---

## 5. S3

S3 → **Create bucket**

- Nombre: `poolmanager-profile-pictures-<ACCOUNT_ID>`
- Region: us-east-1
- Resto por defecto (Block all public access queda activado) → Create

Anotá el nombre como `BUCKET`.

---

## 6. Elastic IP + EC2 (Keycloak)

### 6.1 Elastic IP

EC2 → Network & Security → **Elastic IPs** → Allocate Elastic IP address → Allocate.

Anotá la IP → es tu `KC_IP`. **La necesitás antes de lanzar la instancia.**

### 6.2 Instancia

EC2 → Instances → **Launch instances**

- Name: `poolmanager-keycloak`
- AMI: **Amazon Linux 2023**
- Instance type: `t3.small`
- Key pair: *Proceed without a key pair*
- **Network settings → Edit → Create security group**
  - Nombre: `poolmanager-keycloak-sg`
  - Borrá la regla SSH
  - Add security group rule: Type `Custom TCP`, Port `8080`, Source `0.0.0.0/0`
- **Advanced details** → bajá hasta **User data** → pegá esto, reemplazando `TU_ELASTIC_IP`:

```bash
#!/bin/bash
dnf install -y docker
systemctl enable --now docker
docker volume create kcdata
docker run -d --name keycloak --restart always -p 8080:8080 \
  -e KC_BOOTSTRAP_ADMIN_USERNAME=admin \
  -e KC_BOOTSTRAP_ADMIN_PASSWORD='CambiameYa_2026!' \
  -e KC_HOSTNAME=http://TU_ELASTIC_IP:8080 \
  -e KC_HOSTNAME_STRICT=false \
  -e KC_HTTP_ENABLED=true \
  -v kcdata:/opt/keycloak/data \
  quay.io/keycloak/keycloak:26.4 start-dev
```

**Launch instance**

### 6.3 Asociar la IP

EC2 → Elastic IPs → seleccioná la tuya → Actions → **Associate Elastic IP address** → Instance: `poolmanager-keycloak` → Associate.

**Verificá** (esperá ~2 min): abrí `http://TU_ELASTIC_IP:8080` en el navegador. Tiene que cargar la pantalla de login de Keycloak.

---

## 7. Roles IAM

### 7.1 Task execution role

IAM → Roles → **Create role**

- Trusted entity: **AWS service** → Use case: **Elastic Container Service** → **Elastic Container Service Task** → Next
- Permissions: buscá y marcá `AmazonECSTaskExecutionRolePolicy` → Next
- Role name: `ecsTaskExecutionRole` → Create

> Si ya existe, saltealo.

### 7.2 Infrastructure role

IAM → Roles → **Create role**

- Trusted entity: **Custom trust policy** → pegá:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Principal": { "Service": "ecs.amazonaws.com" },
      "Action": "sts:AssumeRole"
    }
  ]
}
```

- Next → marcá `AmazonECSInfrastructureRoleforExpressGatewayServices` → Next
- Role name: `ecsInfrastructureRoleForExpressServices` → Create

### 7.3 Policy de la app

IAM → Policies → **Create policy** → pestaña **JSON** → pegá reemplazando los 4 placeholders:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": "rds-db:connect",
      "Resource": "arn:aws:rds-db:us-east-1:ACCOUNT_ID:dbuser:DB_RES_ID/postgres"
    },
    {
      "Effect": "Allow",
      "Action": ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"],
      "Resource": "arn:aws:s3:::BUCKET/*"
    },
    {
      "Effect": "Allow",
      "Action": ["s3:ListBucket", "s3:GetBucketLocation"],
      "Resource": "arn:aws:s3:::BUCKET"
    }
  ]
}
```

Name: `poolmanager-rds-s3` → Create policy

> `DB_RES_ID` es el **Resource ID** (`cluster-XXXX`), no `poolmanager-db`. Si ponés el nombre, la conexión falla con un error que no menciona IAM.

### 7.4 Task role

IAM → Roles → **Create role**

- **AWS service** → **Elastic Container Service** → **Elastic Container Service Task** → Next
- Marcá `poolmanager-rds-s3` → Next
- Role name: `poolmanager-task-role` → Create

---

## 8. Migraciones

Consola → ícono **CloudShell** (arriba a la derecha). Pegá:

```bash
export DB_HOST=$(aws rds describe-db-clusters --db-cluster-identifier poolmanager-db \
  --query 'DBClusters[0].Endpoint' --output text)
export PGPASSWORD=$(aws rds generate-db-auth-token \
  --hostname $DB_HOST --port 5432 --username postgres --region us-east-1)
psql "host=$DB_HOST port=5432 dbname=postgres user=postgres sslmode=require"
```

En el prompt `postgres=>` pegá todo el contenido de `migrate.sql` y Enter.

Verificá con `\dt` → tienen que aparecer `Players`, `Matches`, `__EFMigrationsHistory`.

Salí con `\q`.

---

## 9. Servicio ECS Express

ECS → **Create** → elegí **Express** (no "Service" clásico).

- Service name: `poolmanager-api`
- Container image: pegá el URI de ECR con tag `latest`:
  `ACCOUNT_ID.dkr.ecr.us-east-1.amazonaws.com/poolmanager-api:latest`
- Container port: `8080`
- Health check path: `/health`
- CPU: `0.5 vCPU` · Memory: `1 GB`
- Task execution role: `ecsTaskExecutionRole`
- Infrastructure role: `ecsInfrastructureRoleForExpressServices`
- Task role: `poolmanager-task-role`

**Environment variables** — agregá una por una (tipo *Environment variable*, no *Secret*):

| Key | Value |
|---|---|
| `AWS__UseIamAuth` | `true` |
| `ConnectionStrings__DefaultConnection` | `Host=DB_HOST;Port=5432;Database=postgres;Username=postgres;SSL Mode=Require;Trust Server Certificate=true;GSS Encryption Mode=Disable` |
| `Keycloak__Authority` | `http://KC_IP:8080/realms/poolmanager` |
| `Keycloak__ClientId` | `poolmanager-api` |
| `S3__ServiceUrl` | *(dejar vacío o no agregarla)* |
| `S3__Region` | `us-east-1` |
| `S3__BucketName` | `BUCKET` |
| `S3__ForcePathStyle` | `false` |
| `ASPNETCORE_ENVIRONMENT` | `Production` |

**Create**

Tarda 2-4 min. Cuando quede `ACTIVE`, la URL aparece en la pantalla del servicio (`https://poolmanager-api-XXXX.ecs.us-east-1.on.aws`). Anotala como `API_URL`.

**Verificá:** abrí `API_URL/health` en el navegador → `Healthy`.

### Si no levanta

ECS → `poolmanager-api` → pestaña **Logs**.

| Log | Causa |
|---|---|
| `Npgsql` timeout / cuelgue al arrancar | Falta `GSS Encryption Mode=Disable` |
| `PAM authentication failed` | ARN de `rds-db:connect` mal (usaste el nombre en vez del Resource ID) |
| `SignatureDoesNotMatch` en `/storage/*` | Falta `poolmanager-rds-s3` en el task role, o `S3__BucketName` no coincide |
| Tarea que arranca y muere en loop | Excepción en `EnsureBucketExists()` — revisá permisos de S3 |

---

## 10. Realm de Keycloak

`http://KC_IP:8080` → admin / `CambiameYa_2026!`

1. **Create realm** → nombre: `poolmanager`
2. **Clients → Create client**
   - Client ID: `poolmanager-api`
   - Client authentication: **ON**
   - Marcá **Standard flow** y **Direct access grants**
3. **Client scopes** → `poolmanager-api-dedicated` → **Add mapper → By configuration → Audience**
   - Included Client Audience: `poolmanager-api`
   - Add to access token: **ON**

   > Sin este mapper el token no lleva `aud` y la API lo rechaza (`ValidateAudience = true`). Es el error más común.
4. **Realm roles → Create role** → `admin`
5. **Users → Add user** → pestaña **Credentials** → set password (Temporary: OFF) → pestaña **Role mapping** → asignale `admin`
6. **Clients → poolmanager-api → Credentials** → copiá el **Client secret**

---

## Verificación final

Sacá un token:

```powershell
$KC_IP = "TU_ELASTIC_IP"
$body = @{
  client_id     = "poolmanager-api"
  client_secret = "<client secret>"
  username      = "<usuario>"
  password      = "<password>"
  grant_type    = "password"
}
$tok = (Invoke-RestMethod -Method Post -Body $body `
  -Uri "http://${KC_IP}:8080/realms/poolmanager/protocol/openid-connect/token").access_token
```

Probá:

```powershell
$API = "https://TU_API_URL"
$H = @{ Authorization = "Bearer $tok" }

curl.exe "$API/health"                                                      # Healthy
curl.exe -i "$API/players"                                                  # 401
Invoke-RestMethod -Uri "$API/players/me" -Headers $H                        # 200
Invoke-RestMethod -Uri "$API/matches" -Headers $H                           # la base responde
Invoke-RestMethod -Uri "$API/storage/upload-url?fileName=t.jpg" -Headers $H # S3 firma
```

Cerrá el loop del CI: hacé cualquier commit y push → el job `deploy` ahora tiene que quedar verde.

---

## Costo

| | ~mensual |
|---|---|
| ECS Express (0.5 vCPU / 1 GB) | $18 |
| Application Load Balancer | $17 |
| EC2 t3.small | $15 |
| Aurora Serverless v2 (free tier 1er año) | $0–15 |
| S3 + ECR + CloudWatch | $2 |
| **Total** | **~$52–67** |

---

## Borrar todo

En este orden:

1. ECS → `poolmanager-api` → Delete
2. EC2 → Instances → `poolmanager-keycloak` → Terminate
3. EC2 → **Elastic IPs** → Release *(si no la liberás te la siguen cobrando)*
4. RDS → `poolmanager-db` → Delete (skip final snapshot)
5. S3 → vaciar el bucket → Delete
6. ECR → `poolmanager-api` → Delete
7. IAM → borrar `poolmanager-deployer` y los 3 roles

---

## Fuentes

- [ECS Express Mode — consola](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/express-service-first-run.html)
- [ECS Express Mode — overview](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/express-service-overview.html)
- [Aurora PostgreSQL express configuration](https://docs.aws.amazon.com/AmazonRDS/latest/AuroraUserGuide/CHAP_GettingStartedAurora.AuroraPostgreSQL.ExpressConfig.html)
- [Npgsql — GSS Encryption Mode](https://www.npgsql.org/doc/security.html)
- [Keycloak — hostname v2](https://www.keycloak.org/server/hostname)
