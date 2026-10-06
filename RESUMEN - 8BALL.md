# Resumen del Proyecto — 8-Ball Pool Manager API

Guia personal para la presentacion del challenge. Contiene todo lo necesario para levantar, probar y explicar el proyecto.

---

## Que hace la API

API REST para gestionar jugadores y partidas de pool (billar 8-ball). Permite registrar jugadores automaticamente via Keycloak, crear partidas entre ellos, declarar ganadores (con actualizacion de ranking), y subir fotos de perfil a S3 mediante URLs pre-firmadas.

---

## Arquitectura

```
[Postman / Cliente]
        |
        | HTTPS
        v
[ECS Express Mode — API .NET 10]
        |
        |--- JWT validation ---> [EC2 — Keycloak (Elastic IP)]
        |
        |--- IAM Auth (token rotativo cada 14 min) ---> [Aurora PostgreSQL Serverless]
        |
        |--- Pre-signed URLs ---> [S3 — poolmanager-profile-pictures]
```

El pipeline CI/CD (GitHub Actions) se dispara en cada push a main: build, test, push imagen a ECR, redeploy en ECS.

---

## Paso 0 — Levantar los servicios de AWS

Si los servicios estan pausados, levantarlos en este orden:

1. **Aurora PostgreSQL** — RDS → Clusters → seleccionar cluster → Actions → Start
2. **EC2 (Keycloak)** — EC2 → Instances → seleccionar instancia → Instance state → Start instance
3. **ECS** — ECS → Clusters → default → servicio poolmanager-api → Update → Desired tasks: 1 → Update

Esperar unos minutos a que todo este healthy. Verificar:

- **Keycloak**: abrir `http://<ELASTIC_IP>:8080` en el navegador (debe mostrar la pagina de Keycloak)
- **API Health**: `GET https://po-f4ee17d91c17431e9de7c8eb93a36a1c.ecs.us-east-1.on.aws/health` (debe devolver "Healthy")
- **Swagger**: `https://po-f4ee17d91c17431e9de7c8eb93a36a1c.ecs.us-east-1.on.aws/swagger`

> NOTA: Si la IP publica de tu red cambio, actualizar los security groups de EC2 (puertos 22 y 8080) con tu nueva IP.

---

## Paso 1 — Obtener un token JWT de Keycloak

En Postman:

- **Metodo**: POST
- **URL**: `http://<ELASTIC_IP>:8080/realms/poolmanager/protocol/openid-connect/token`
- **Body**: x-www-form-urlencoded

| Key           | Value             |
|---------------|-------------------|
| grant_type    | password          |
| client_id     | poolmanager-api   |
| username      | testuser          |
| password      | test1234          |

**Respuesta esperada**: un JSON con `access_token` y `refresh_token`. Copiar el `access_token`.

> IMPORTANTE: Los tokens expiran rapido. Si un endpoint devuelve 401, generar un token nuevo.

Para todos los endpoints siguientes, agregar en Postman:
- Tab **Authorization** → Type: **Bearer Token** → pegar el access_token

---

## Paso 2 — Probar Players

### 2.1 — Auto-registro (GET /players/me)

- **Metodo**: GET
- **URL**: `https://po-f4ee17d91c17431e9de7c8eb93a36a1c.ecs.us-east-1.on.aws/players/me`
- **Auth**: Bearer token

**Respuesta esperada** (primera vez crea el jugador automaticamente):
```json
{
    "id": 1,
    "name": "testuser",
    "ranking": 0,
    "preferredCue": null,
    "profilePictureUrl": "pending"
}
```

**Que esta pasando internamente**: El controller extrae el `sub` (Keycloak ID) del token JWT. Busca un player con ese ID en Aurora. Si no existe, lo crea automaticamente con el nombre del token.

### 2.2 — Actualizar perfil (PATCH /players/me)

- **Metodo**: PATCH
- **URL**: `https://po-f4ee17d91c17431e9de7c8eb93a36a1c.ecs.us-east-1.on.aws/players/me`
- **Body** (JSON):
```json
{
    "name": "Fabrizio",
    "preferredCue": "Viking Valhalla"
}
```

**Respuesta esperada**: el player actualizado con los nuevos campos.

### 2.3 — Crear segundo jugador

Para probar matches necesitas dos jugadores. Genera un token con el segundo usuario:

POST al mismo endpoint de Keycloak pero con:
| Key       | Value      |
|-----------|------------|
| username  | testuser2  |
| password  | test1234   |

Luego hace GET `/players/me` con ese token para que se auto-registre.

> Si testuser2 no existe en Keycloak, crearlo: Keycloak admin → realm poolmanager → Users → Add user (username: testuser2, email: test2@test.com, email verified: ON, Credentials: test1234, Temporary: OFF).

---

## Paso 3 — Probar Matches

### 3.1 — Crear una partida (POST /matches)

- **Metodo**: POST
- **URL**: `https://po-f4ee17d91c17431e9de7c8eb93a36a1c.ecs.us-east-1.on.aws/matches`
- **Auth**: Bearer token (cualquiera de los dos usuarios)
- **Body** (JSON):
```json
{
    "player1Id": 1,
    "player2Id": 2,
    "startTime": "2026-09-01T15:00:00Z"
}
```

> NOTA: Si el segundo jugador tiene un ID diferente (ej: 34), usar ese ID.

**Respuesta esperada**:
```json
{
    "id": 1,
    "player1Id": 1,
    "player1Name": "testuser",
    "player2Id": 2,
    "player2Name": "testuser2",
    "startTime": "2026-09-01T15:00:00Z",
    "endTime": null,
    "winnerId": null,
    "winnerName": null,
    "tableNumber": null,
    "status": "upcoming"
}
```

### 3.2 — Listar partidas (GET /matches)

- **Metodo**: GET
- **URL**: `https://po-f4ee17d91c17431e9de7c8eb93a36a1c.ecs.us-east-1.on.aws/matches`

**Respuesta esperada**: array con las partidas creadas.

### 3.3 — Ver una partida especifica (GET /matches/:id)

- **Metodo**: GET
- **URL**: `https://po-f4ee17d91c17431e9de7c8eb93a36a1c.ecs.us-east-1.on.aws/matches/1`

### 3.4 — Declarar ganador (PATCH /matches/:id)

- **Metodo**: PATCH (no PUT)
- **URL**: `https://po-f4ee17d91c17431e9de7c8eb93a36a1c.ecs.us-east-1.on.aws/matches/1`
- **Body** (JSON):
```json
{
    "winnerId": 1
}
```

**Respuesta esperada**: el match con `winnerId` seteado y `status: "completed"`.

**Verificar ranking**: Hacer GET `/players/me` — el ranking debe haber subido de 0 a 1.

### 3.5 — Validaciones que se pueden demostrar

**Un jugador no puede jugar contra si mismo:**
```json
{
    "player1Id": 1,
    "player2Id": 1,
    "startTime": "2026-09-02T15:00:00Z"
}
```
Respuesta esperada: error "contra si mismo".

**Double-booking (dos partidas solapadas para el mismo jugador):**
Crear una partida con horario que se superpone con una existente. Respuesta esperada: error 409 Conflict con mensaje sobre horario.

---

## Paso 4 — Probar Storage (S3)

### 4.1 — Obtener URL de subida (GET /storage/upload-url)

- **Metodo**: GET
- **URL**: `https://po-f4ee17d91c17431e9de7c8eb93a36a1c.ecs.us-east-1.on.aws/storage/upload-url?fileName=foto.jpg`
- **Auth**: Bearer token

**Respuesta esperada**:
```json
{
    "uploadUrl": "https://s3.us-east-1.amazonaws.com/poolmanager-profile-pictures/players/<guid>/foto.jpg?X-Amz-...",
    "key": "players/<guid>/foto.jpg"
}
```

Guardar la `key` — la vas a necesitar despues.

### 4.2 — Subir imagen a S3

- **Metodo**: PUT
- **URL**: la `uploadUrl` completa del paso anterior (con todos los query params)
- **Auth**: NINGUNA (la URL ya tiene la firma)
- **Body**: Binary → seleccionar una imagen de tu PC

**Respuesta esperada**: 200 OK sin body (o 200 con body vacio).

### 4.3 — Actualizar perfil con la foto

- **Metodo**: PATCH
- **URL**: `https://po-f4ee17d91c17431e9de7c8eb93a36a1c.ecs.us-east-1.on.aws/players/me`
- **Body** (JSON):
```json
{
    "profilePictureUrl": "https://s3.us-east-1.amazonaws.com/poolmanager-profile-pictures/players/<guid>/foto.jpg"
}
```

Usar la URL base (sin los query params `?X-Amz-...`).

### 4.4 — Obtener URL de descarga (GET /storage/download-url)

- **Metodo**: GET
- **URL**: `https://po-f4ee17d91c17431e9de7c8eb93a36a1c.ecs.us-east-1.on.aws/storage/download-url?key=players/<guid>/foto.jpg`
- **Auth**: Bearer token

**Respuesta esperada**: un JSON con `downloadUrl` — abrir esa URL en el navegador deberia mostrar la imagen.

---

## Paso 5 — Probar CI/CD

1. Hacer un cambio menor en el codigo (ej: cambiar un comentario en un .cs)
2. Commit y push a main
3. Ir a GitHub → pestaña Actions → ver que el pipeline se ejecuta
4. Debe pasar: Restore → Build → Test → Login ECR → Build imagen → Push a ECR → Redeploy ECS
5. Verificar que los archivos `.md` NO disparan el pipeline (paths-ignore configurado)

---

## Paso 6 — Probar Tests localmente

```bash
dotnet test PoolManager.slnx
```

Esto ejecuta los tests de xUnit con Bogus (datos fake) usando una base de datos in-memory. Los tests cubren:

- Crear jugador
- Crear partida
- Rechazar partida contra uno mismo
- Rechazar double-booking
- Permitir partidas que no se solapan
- Declarar ganador y actualizar ranking
- Rechazar ganador invalido

---

## Paso 7 — Pausar servicios de AWS (para no generar costos)

En orden inverso:

1. **ECS** → Clusters → default → servicio poolmanager-api → Update → Desired tasks: **0**
2. **EC2** → Instances → seleccionar instancia → Instance state → **Stop instance**
3. **Aurora** → RDS → Clusters → seleccionar cluster → Actions → **Stop temporarily** (se reinicia solo a los 7 dias)

ECR y S3 se pueden dejar (costo minimo, centavos).

---

## Datos de acceso

| Recurso | Dato |
|---------|------|
| API URL | `https://po-f4ee17d91c17431e9de7c8eb93a36a1c.ecs.us-east-1.on.aws` |
| Swagger | `https://po-f4ee17d91c17431e9de7c8eb93a36a1c.ecs.us-east-1.on.aws/swagger` |
| Keycloak | `http://<ELASTIC_IP>:8080` |
| Keycloak admin | (credenciales actualizadas) |
| Keycloak test user 1 | testuser / test1234 |
| Keycloak test user 2 | testuser2 / test1234 |
| GitHub repo | https://github.com/Fabrizio-shipped-it/8-ball-API |
| AWS region | us-east-1 |
| ECR | 959015414570.dkr.ecr.us-east-1.amazonaws.com/poolmanager-api |
| Aurora endpoint | poolmanager-db.cluster-ckf2gq4ok7ud.us-east-1.rds.amazonaws.com |
| S3 bucket | poolmanager-profile-pictures |
| ECS cluster/service | default / poolmanager-api |

---

## Seguridad — Puntos clave para explicar

1. **IAM Auth para Aurora** — No hay contraseñas en la connection string. Se genera un token IAM cada 14 minutos con `RDSAuthTokenGenerator`. El token se usa como password temporal.
2. **Pre-signed URLs para S3** — El cliente nunca tiene acceso directo al bucket. La API genera URLs firmadas con expiracion de 15 min (upload) o 24 hs (download).
3. **JWT via Keycloak** — Cada request lleva un token firmado. La API valida la firma contra las claves publicas del realm de Keycloak.
4. **Rate limiting** — 100 requests/minuto general, 5 requests/15 minutos para auth. Protege contra abuso.
5. **Manejo de excepciones** — Nunca se exponen stack traces al cliente. Se devuelve un JSON generico `{"error": "Error interno del servidor"}` y se loguea internamente.
6. **Principio de minimo privilegio** — El IAM user `poolmanager-deployer` solo tiene los permisos justos: ECR (push imagenes), S3 (storage), ECS (deploy), RDS connect (auth a Aurora).
7. **Security groups** — Keycloak solo acepta trafico del security group de ECS y de la IP del desarrollador. No hay `0.0.0.0/0`.
8. **SSL obligatorio** — La conexion a Aurora usa `Ssl Mode=Require`.

---

## Errores comunes y soluciones rapidas

| Problema | Causa probable | Solucion |
|----------|---------------|----------|
| 401 Unauthorized | Token expirado | Generar token nuevo en Keycloak |
| 504 Gateway Timeout | Keycloak caido o security group mal | Verificar EC2 running y SG con IP actual |
| 500 Internal Server Error | Aurora caido o credenciales AWS | Verificar Aurora running, check logs en ECS |
| SSH connection timed out | IP publica cambio | Actualizar security group puerto 22 con IP actual |
| Pipeline no se dispara | Solo cambios en .md | Correcto, paths-ignore funciona |
| 405 Method Not Allowed | Usando PUT en vez de PATCH | Cambiar metodo a PATCH |
