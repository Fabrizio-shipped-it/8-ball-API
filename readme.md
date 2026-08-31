# 8-Ball Pool Manager API

API REST para gestionar jugadores y partidas de pool (billar), desarrollada con .NET 10 y arquitectura MVC.

## Tech Stack.
 
- **.NET 10** — Web API con Controllers
- **Entity Framework Core** — ORM con PostgreSQL
- **Keycloak** — Autenticación y autorización (JWT)
- **MinIO** — Almacenamiento S3-compatible para fotos de perfil (local)
- **xUnit + Bogus** — Testing
- **Docker Compose** — Entorno local
- **AWS ECS Express Mode** — Deploy en la nube
- **AWS Aurora PostgreSQL Serverless** — Base de datos cloud con IAM auth
- **AWS S3** — Storage de imágenes en la nube
- **AWS ECR** — Registro de imágenes Docker
- **GitHub Actions** — CI/CD pipeline

**API en la nube:** https://po-1db0c94e9e5e492fb227412482899df0.ecs.us-east-1.on.aws 
> La URL base no tiene endpoint en `/`. Para interactuar con la API, usar Swagger o los endpoints listados abajo.

**Swagger UI:** https://po-1db0c94e9e5e492fb227412482899df0.ecs.us-east-1.on.aws/swagger

**Keycloak:** https://52-73-23-203.sslip.io (HTTPS via Caddy + Let's Encrypt sobre sslip.io)

## Arquitectura Cloud (AWS)

La API está desplegada en AWS con la siguiente arquitectura:

- **ECS Express Mode** — Contenedor de la API con deployment automático via CI/CD
- **Aurora PostgreSQL Serverless** — Base de datos con autenticación IAM (sin contraseñas, tokens rotativos cada 14 min)
- **EC2** — Keycloak para autenticación JWT
- **S3** — Almacenamiento de fotos de perfil con URLs pre-firmadas
- **ECR** — Registro de imágenes Docker

```
[Cliente] → HTTPS → [ECS Express Mode (API)]
                          ├── JWT validation → [EC2 (Keycloak)]
                          ├── IAM Auth → [Aurora PostgreSQL Serverless]
                          └── Pre-signed URLs → [S3]
```

### Seguridad en AWS

- Autenticación IAM para Aurora (sin contraseñas en connection strings)
- Tokens RDS generados con `RDSAuthTokenGenerator`, rotación cada 14 minutos
- Security group de Keycloak con 8080, 80 y 443 abiertos a internet. Es necesario: las
  tareas de Fargate tienen IP pública dinámica y Let's Encrypt valida por el puerto 80.
  El acceso real lo controla Keycloak, no la red
- Principio de mínimo privilegio en IAM (`poolmanager-deployer` solo tiene ECR, S3, ECS, RDS connect)
- SSL obligatorio en conexión a Aurora
- Elastic IP en EC2 para IP fija de Keycloak, con HTTPS via Caddy + Let's Encrypt sobre sslip.io

## Requisitos previos

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop)

## Setup local

1. **Clonar el repositorio:**

```bash
git clone https://github.com/Fabrizio-shipped-it/8-ball-API.git
cd 8-ball-API
```

2. **Crear el archivo `.env`** en la raíz con estas variables:

```env
POSTGRES_USER=poolmanager
POSTGRES_PASSWORD=tu_password
POSTGRES_DB=poolmanager_db
KEYCLOAK_ADMIN=admin
KEYCLOAK_ADMIN_PASSWORD=admin
KC_DB_PASSWORD=tu_password
MINIO_ROOT_USER=minioadmin
MINIO_ROOT_PASSWORD=minioadmin
```

3. **Crear `appsettings.Development.json`** en la raíz (está en `.gitignore`, no viaja
   dentro de la imagen Docker):

```json
{
  "S3": {
    "AccessKey": "minioadmin",
    "SecretKey": "minio_secret_123"
  }
}
```

   Estas claves antes estaban hardcodeadas en `appsettings.json` y terminaban dentro de la
   imagen. En AWS eso hacía que la API las tomara como válidas, ignorara el task role de ECS
   y firmara todas las URLs con credenciales de MinIO. Si el archivo no existe, `S3Service`
   cae en la cadena de credenciales por defecto (que es justo lo que queremos en AWS).

4. **Levantar los contenedores:**

```bash
docker compose up -d
```

Esto inicia PostgreSQL, Keycloak y MinIO.

5. **Configurar Keycloak:**

- Ir a `http://localhost:8080` y loguearse con las credenciales de admin
- Crear un realm llamado `poolmanager`
- Crear un client llamado `poolmanager-api` (Client authentication: ON, flow: Standard + Direct access grants)
- Agregar un Audience mapper en el client scope dedicado (Included Client Audience: `poolmanager-api`)
- Crear un rol de realm `admin` y asignarlo a los usuarios que lo necesiten

6. **Ejecutar la API:**

```bash
dotnet run
```

La API estará disponible en `http://localhost:5225` y Swagger en `http://localhost:5225/swagger`.

## Endpoints principales

### Players

| Método | Ruta | Auth | Descripción |
|--------|------|------|-------------|
| GET | `/players/me` | Usuario | Obtener/auto-registrar perfil propio |
| PATCH | `/players/me` | Usuario | Actualizar perfil propio (`profilePictureKey` se valida contra tu carpeta) |
| GET | `/players` | Admin | Listar jugadores (filtro por `?name=`) |
| POST | `/players` | Admin | Crear jugador |
| DELETE | `/players/{id}` | Admin | Eliminar jugador |

### Matches

| Método | Ruta | Auth | Descripción |
|--------|------|------|-------------|
| POST | `/matches` | Participante | Crear partida (solo si sos uno de los dos jugadores) |
| GET | `/matches` | Usuario | Listar **tus** partidas (`?date=`, `?status=`). `?all=true` solo admin |
| GET | `/matches/{id}` | Participante | Ver detalle (404 si no participás) |
| PATCH | `/matches/{id}` | Participante | Actualizar partida / asignar ganador |
| DELETE | `/matches/{id}` | Admin | Eliminar partida |

### Otros

| Método | Ruta | Auth | Descripción |
|--------|------|------|-------------|
| GET | `/storage/upload-url?fileName=&contentType=` | Usuario | URL pre-firmada para subir (5 min, key bajo `players/{tuId}/`) |
| GET | `/storage/download-url?key=` | Usuario | URL pre-firmada para descargar (1 h, solo keys tuyas o fotos publicadas) |
| GET | `/health` | Público | Liveness. No toca la base — es el health check del ALB |
| GET | `/health/ready` | Público | Readiness. Verifica la conexión a la base |

## Tests

```bash
dotnet test PoolManager.slnx
```

## Seguridad implementada

- Autenticación JWT vía Keycloak sobre HTTPS
- Autorización por roles (admin/usuario). Los roles de Keycloak vienen anidados en
  `realm_access.roles`; se mapean a `ClaimTypes.Role` en `OnTokenValidated`, sin eso
  `[Authorize(Roles = "admin")]` nunca matchea
- **Control de pertenencia en Matches**: solo los participantes (o un admin) pueden ver,
  modificar o declarar ganador. A un tercero se le devuelve 404, no 403, para no confirmar
  que la partida existe
- **Aislamiento en S3**: las keys viven en `players/{playerId}/`. La API solo firma URLs
  para keys propias o para fotos de perfil publicadas, y verifica con `GetObjectMetadata`
  que el objeto exista antes de guardarlo
- La base guarda la **key** de S3, no la URL: el nombre del bucket y la región no se
  exponen al cliente
- Validación de input con Data Annotations
- Formato de error uniforme `{ "error": "..." }`, incluidos los fallos de deserialización
  (que por defecto filtran path del JSON, línea y tipo .NET esperado)
- Rate limiting: 100 req/min general, y 20 req/5min sobre `/storage`, que es el vector
  real de abuso (cada llamada firma una URL que habilita a escribir en el bucket)
- Manejo global de excepciones (sin exponer stack traces), con un `traceId` en el 500
  que permite correlacionar el reporte de un usuario con el log en CloudWatch
- Respuestas de error uniformes también en los códigos que el framework emite sin cuerpo:
  401, 403, 404 de ruta inexistente, 405 y 429
- Índice en StartTime para performance
- Detección de double-booking, tanto de jugadores como de **mesas**: dos partidas no
  pueden compartir mesa en horarios solapados
- Validación de horarios: la hora de fin debe ser posterior a la de inicio, una partida
  no puede durar más de 12 h, y no se puede agendar en el pasado
- Reintento automático ante fallas transitorias de la base (`EnableRetryOnFailure`), que
  cubre los cold starts de Aurora Serverless
- Auto-registro resistente a concurrencia: dos logins simultáneos del mismo usuario nuevo
  no rompen con violación de unicidad
- Logging estructurado con ILogger

### Limitación conocida: URLs pre-firmadas

Una URL pre-firmada de S3 es válida hasta que expira y **no se puede invalidar después de
usarla**: el single-use no existe de forma nativa. Se mitiga con una ventana corta (5 min),
una key única por request (dos subidas nunca se pisan) y la confirmación server-side de que
el objeto existe antes de asociarlo al perfil.

## CI/CD

El pipeline (`.github/workflows/ci.yml`) se ejecuta en cada push a `main`:

1. **CI** — Restore, build y test del proyecto
2. **CD** — Build de la imagen, push a ECR con dos tags (el SHA del commit y `latest`),
   y despliegue con `update-express-gateway-service` pasando la tag del SHA.

El paso de despliegue lee el `primaryContainer` actual del servicio y le reemplaza
únicamente el campo `image`, de modo que las variables de entorno (connection string,
Keycloak, S3) sobreviven al deploy.

Los secrets `AWS_ACCESS_KEY_ID` y `AWS_SECRET_ACCESS_KEY` deben estar configurados en GitHub → Settings → Secrets → Actions.

## Deploy manual

Si necesitás desplegar sin pasar por el pipeline:

```bash
ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
REGISTRY=$ACCOUNT_ID.dkr.ecr.us-east-1.amazonaws.com
TAG=$(git rev-parse HEAD)

aws ecr get-login-password --region us-east-1 | docker login --username AWS --password-stdin $REGISTRY

docker build -t $REGISTRY/poolmanager-api:$TAG .
docker push $REGISTRY/poolmanager-api:$TAG

SERVICE_ARN="arn:aws:ecs:us-east-1:$ACCOUNT_ID:service/default/poolmanager-api"

# Se parte de la config actual del contenedor y se le cambia SOLO la imagen,
# para no perder las variables de entorno.
aws ecs describe-express-gateway-service --service-arn "$SERVICE_ARN" \
  --query 'service.activeConfigurations[0].primaryContainer' --output json > container.json

jq --arg img "$REGISTRY/poolmanager-api:$TAG" '.image = $img' container.json > container-new.json

aws ecs update-express-gateway-service \
  --service-arn "$SERVICE_ARN" \
  --primary-container file://container-new.json \
  --monitor-resources
```

> **No uses `aws ecs update-service --force-new-deployment`.** ECS Express Mode fija el
> digest de la imagen cuando se crea el servicio, así que ese comando relanza siempre el
> mismo binario: el deploy termina en verde y no cambia absolutamente nada. Hay que pasarle
> la imagen nueva explícitamente con la API de Express Mode, y con una tag distinta en cada
> deploy (por eso se usa el SHA del commit y no `latest`).
