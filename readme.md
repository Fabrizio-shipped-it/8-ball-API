# 8-Ball Pool Manager API

API REST para gestionar jugadores y partidas de pool (billar), desarrollada con .NET 10 y arquitectura MVC.

## Tech Stack

- **.NET 10** — Web API con Controllers
- **Entity Framework Core** — ORM con PostgreSQL
- **Keycloak** — Autenticación y autorización (JWT)
- **MinIO** — Almacenamiento S3-compatible para fotos de perfil
- **xUnit + Bogus** — Testing
- **Docker Compose** — Entorno local

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

3. **Levantar los contenedores:**

```bash
docker compose up -d
```

Esto inicia PostgreSQL, Keycloak y MinIO.

4. **Configurar Keycloak:**

- Ir a `http://localhost:8080` y loguearse con las credenciales de admin
- Crear un realm llamado `poolmanager`
- Crear un client llamado `poolmanager-api` (Client authentication: ON, flow: Standard + Direct access grants)
- Agregar un Audience mapper en el client scope dedicado (Included Client Audience: `poolmanager-api`)
- Crear un rol de realm `admin` y asignarlo a los usuarios que lo necesiten

5. **Ejecutar la API:**

```bash
dotnet run
```

La API estará disponible en `http://localhost:5225` y Swagger en `http://localhost:5225/swagger`.

## Endpoints principales

### Players

| Método | Ruta | Auth | Descripción |
|--------|------|------|-------------|
| GET | `/players/me` | Usuario | Obtener/auto-registrar perfil propio |
| PATCH | `/players/me` | Usuario | Actualizar perfil propio |
| GET | `/players` | Admin | Listar jugadores (filtro por `?name=`) |
| POST | `/players` | Admin | Crear jugador |
| DELETE | `/players/{id}` | Admin | Eliminar jugador |

### Matches

| Método | Ruta | Auth | Descripción |
|--------|------|------|-------------|
| POST | `/matches` | Usuario | Crear partida |
| GET | `/matches` | Usuario | Listar partidas (filtros: `?date=`, `?status=`) |
| GET | `/matches/{id}` | Usuario | Ver detalle de partida |
| PATCH | `/matches/{id}` | Usuario | Actualizar partida / asignar ganador |
| DELETE | `/matches/{id}` | Admin | Eliminar partida |

### Otros

| Método | Ruta | Auth | Descripción |
|--------|------|------|-------------|
| GET | `/storage/upload-url?fileName=` | Usuario | URL pre-firmada para subir imagen |
| GET | `/storage/download-url?fileName=` | Usuario | URL pre-firmada para descargar imagen |
| GET | `/health` | Público | Healthcheck |

## Tests

```bash
dotnet test PoolManager.slnx
```

## Seguridad implementada

- Autenticación JWT vía Keycloak
- Autorización por roles (admin/usuario)
- Validación de input con Data Annotations
- Rate limiting (100 req/min general, 5 req/15min auth)
- Manejo global de excepciones (sin exponer stack traces)
- Índice en StartTime para performance
- Detección de double-booking en partidas
- Logging estructurado con ILogger