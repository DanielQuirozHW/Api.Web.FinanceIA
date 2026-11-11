# Verificación de esquema antes de publicar

## Qué problema resuelve

Este proyecto es **Database First**. Los cambios de esquema son scripts DDL numerados en
`Api.Web.FinanceIA/docs/database/` que ejecuta el DBA a mano, y **tienen que correr ANTES** del
deploy que los necesita: EF Core mapea las columnas incondicionalmente, así que publicar el
código primero rompe producción con `Invalid column name` en todo SELECT e INSERT contra esa
tabla — o con `Invalid object name` si lo que falta es la tabla entera.

Hasta ahora lo único que sostenía ese orden era el tilde **`ddl_aplicada`** del workflow de
deploy. Eso es una *promesa del operador*, no evidencia: se puede tildar sin haber corrido nada
y el deploy sale igual.

La verificación de esquema convierte esa promesa en una comprobación contra la base.

## Qué hace exactamente

`dotnet Api.Web.FinanceIA.dll --verify-schema` recorre los modelos EF de **este build**
(`FinanciaDbContext` y `AppDbContext`), arma la lista de cada tabla y cada columna que van a
pedirle a la base, y la compara contra `sys.objects` / `sys.columns` de cada base configurada.

**En el deploy sólo se verifica `FinanciaDbContext`**, contra la base Financia de cada cliente. La
base Default compartida (`Credenciales`, `TenantFirebaseConfig`, `ErrorLogs`, el modelo de
`AppDbContext`) la usan todos los portales de la empresa y los releases de este repo no la cambian,
así que el workflow la deja afuera de forma explícita (ver
[En el workflow de deploy](#en-el-workflow-de-deploy)).

Puntos que importan:

- **Es de solo lectura.** Hace un `SELECT` sobre las vistas del catálogo. **No ejecuta ningún
  DDL.** Correr los scripts sigue siendo trabajo del DBA, así que el pipeline nunca necesita
  permisos de `ALTER` sobre producción — le alcanza con `VIEW DEFINITION`.
- **Sale del modelo, no de los `.sql`.** Lo que rompe producción es lo que EF emite, y eso lo
  dice el modelo. Parsear la carpeta de scripts sólo diría qué se pensaba correr.
- **Verifica el binario publicado**, no el código fuente: es exactamente el modelo que se va a
  desplegar.
- **Ignora lo que sobra.** Una tabla que la base tiene y el modelo no mapea no es un error: la
  pregunta es "¿puede funcionar este código?", no "¿coinciden exactamente los esquemas?".
- **Compara nombres, y el TIPO de una lista corta y explícita de columnas.** Por defecto basta con
  que la tabla y la columna existan. Hay un caso que eso no ve: una tabla que YA existe con todos los
  nombres correctos pero con un tipo que el código no puede escribir. `[dbo].[AnalysisEdits]` existe
  desde el 001 con `TenantId UNIQUEIDENTIFIER`, y recién el 081 la pasa a `NVARCHAR(100)`: antes del
  081 todos los nombres coinciden y la primera corrección de balance fallaría al insertar un slug.
  `SchemaExpectation.RequiredColumnTypes` lista esas columnas (hoy, sólo `AnalysisEdits.TenantId` →
  `nvarchar`); el gate lee `TYPE_NAME(system_type_id)` de `sys.columns` y reporta
  `WRONG TYPE … is uniqueidentifier, the code needs nvarchar`. Es una lista a propósito, no una
  comparación de tipos de todo el modelo: agregar el chequeo no cambió el veredicto de ninguna otra
  tabla. No mira nulabilidad, largos, defaults, CHECKs ni índices — para eso están los post-checks
  de cada script.

## Códigos de salida

| Código | Significado | Qué hacer |
|--------|-------------|-----------|
| `0` | La base ya tiene todo lo que este build mapea | Publicar |
| `1` | Falta al menos una tabla o columna, o una columna de `RequiredColumnTypes` tiene otro tipo | Correr el DDL pendiente en **cada** base y reintentar |
| `2` | No se pudo comprobar (sin configuración, o base inalcanzable) | **No es un pase.** Revisar credenciales y firewall |

El código `2` es deliberado: un fallo de red no puede leerse como un gate en verde.

## Configuración

Las connection strings se pasan **por variable de entorno, nunca por línea de comandos** — una
connection string lleva password y la línea de comandos es visible en el listado de procesos y
en los logs de CI.

| Variable | Contenido |
|---|---|
| `FINANCIA_SCHEMA_VERIFY_CONNECTIONS` | Una connection string **por línea**, una por cada base de negocio de tenant. Se ignoran las líneas en blanco y las que empiezan con `#`. |
| `FINANCIA_SCHEMA_VERIFY_DEFAULT_CONNECTION` | La base compartida "Default" (`Credenciales`, `TenantFirebaseConfig`, `ErrorLogs`). El deploy ya no la usa; sirve para comprobarla a mano. |
| `FINANCIA_SCHEMA_VERIFY_SKIP_DEFAULT` | `true` deja la base Default afuera, **sin importar** lo que tengan `FINANCIA_SCHEMA_VERIFY_DEFAULT_CONNECTION` o `ConnectionStrings:Default`. Vacío o ausente equivale a `false`. Cualquier otro valor que no sea `true`/`false` sale con `2`, para que un typo no cambie en silencio qué se verifica. |

Si no se define ninguna, cae a `ConnectionStrings:FinanciaDefault` y `ConnectionStrings:Default`,
que es lo cómodo para correrlo local contra la base de desarrollo. Esas dos salen de
`appsettings.json`, de user-secrets o de variables de entorno (`ConnectionStrings__Default`): en este
modo **no** se carga ningún `appsettings.{Environment}.json`. Por eso el salteo de la Default es un
flag explícito y no "dejar la connection string vacía": lo que traiga alguna de esas fuentes no
puede volver a prender el chequeo.

**El formato multilínea es el que sostiene el modelo una-base-por-cliente.** Cada tenant tiene su
propia base y el DDL se aplica en todas, así que la verificación también: una línea por base.
Cuidado con el modo de falla de una lista mantenida a mano — un cliente que no esté en
`.github/clients.json` no se despliega ni se verifica. Cuando se da de alta un cliente, en el mismo
movimiento se crea su secret de conexión y se agrega su entrada a `clients.json` (pasos completos
en el encabezado de `.github/workflows/deploy.yml`).

### En el workflow de deploy

`deploy.yml` despliega **un cliente por pata** de una matriz armada desde `.github/clients.json`,
y cada pata corre la verificación después de bajar el artifact y **antes** de publicar. Cada pata
arma las variables así:

| Variable | Sale de |
|---|---|
| `FINANCIA_SCHEMA_VERIFY_CONNECTIONS` | El secret que nombra `schema_connection_secret` en la entrada del cliente: la base Financia de **ese** cliente (una connection string por línea, si tuviera más de una). El cliente inicial `sgr-portales` usa `SCHEMA_VERIFY_CONNECTIONS`. |
| `FINANCIA_SCHEMA_VERIFY_SKIP_DEFAULT` | Fijo en `true`. El log del paso lo dice: `Base Default: no se verifica (compartida, fuera de este repo)`. |

**El secret de la base Financia es obligatorio.** Si está vacío o no existe, esa pata falla con un
error que nombra el secret, antes de verificar o publicar nada. Las demás patas siguen. Una base
Financia inalcanzable o una connection string inutilizable siguen dando `2` (INCONCLUSO), y una
columna faltante sigue dando `1`: esa parte del gate no cambió.

**La base Default no se verifica en el deploy.** El secret `SCHEMA_VERIFY_DEFAULT_CONNECTION` ya no
se usa ni se exige, y se puede borrar. La consecuencia: **un script de
`docs/database/default-db/` se corre a mano y se comprueba a mano** (ver
[Correrlo a mano](#correrlo-a-mano)). Ningún deploy lo va a detectar si falta.

Además, **el SQL Server tiene que aceptar la conexión desde el runner de GitHub**. Es el
requisito de infraestructura del gate y no se puede resolver desde el repo. Alternativas, de más
a menos recomendable:

1. Un runner self-hosted dentro de la red que ya llega a Azure SQL.
2. Un paso previo que agregue y luego borre una regla de firewall para la IP del runner
   (requiere `azure/login` con un service principal, que hoy el workflow no usa).
3. Habilitar "Permitir que los servicios de Azure accedan al servidor", que es la opción más
   amplia y la menos recomendable.

**Mientras esos secrets no estén configurados, la pata de ese cliente falla a propósito** con un
mensaje que explica qué falta. Es fail-closed: un gate que se saltea solo no es un gate. Para
publicar igual en una emergencia está el input `omitir_verificacion_esquema`, que deja un
`::warning::` en el log del run.

## Correrlo a mano

Antes de tildar `ddl_aplicada`, o para chequear un ambiente de desarrollo:

```bash
# Contra lo que ya tenés configurado en user-secrets
dotnet run --project Api.Web.FinanceIA/Api.Web.FinanceIA.csproj -- --verify-schema

# Contra varias bases
export FINANCIA_SCHEMA_VERIFY_CONNECTIONS="Server=...;Database=financia-cliente-a;...
Server=...;Database=financia-cliente-b;..."
dotnet run --project Api.Web.FinanceIA/Api.Web.FinanceIA.csproj -- --verify-schema

# Contra la base Default compartida, después de correr un script de docs/database/default-db/
# (el deploy ya no lo comprueba). Sin FINANCIA_SCHEMA_VERIFY_SKIP_DEFAULT. Si tampoco hay una
# base Financia configurada (variable o ConnectionStrings:FinanciaDefault), verifica sólo la Default.
export FINANCIA_SCHEMA_VERIFY_DEFAULT_CONNECTION="Server=...;Database=<base Default>;..."
dotnet run --project Api.Web.FinanceIA/Api.Web.FinanceIA.csproj -- --verify-schema
```

El proceso no levanta la API: entra en modo verificación antes de registrar un solo servicio, así
que no toca Firebase, Claude ni ningún otro proveedor.

## Lo que esto NO hace

- **No ejecuta el DDL.** Es una comprobación, no una migración.
- **En el deploy no verifica la base Default compartida.** Los scripts de
  `docs/database/default-db/` se corren y se comprueban a mano.
- **No verifica tipos, longitudes, índices ni constraints** — sólo presencia de tablas y
  columnas. Un `NVARCHAR(100)` donde el script decía `NVARCHAR(400)` pasa el gate. Cubre el modo
  de falla documentado (`Invalid column name` / `Invalid object name`), que es el que rompe
  producción en el arranque.
- **No sabe del orden con el Portal.** Los scripts que exigen desplegar el Portal primero (040,
  058-060) siguen dependiendo de que el operador lea la nota en `docs/database/README.md`.
- **No descubre las bases solo.** La lista de bases de tenant sale del secret; no hay en el
  código ninguna resolución de conexión por tenant de la que derivarla.

## Archivos

| Archivo | Rol |
|---|---|
| `Clases/SchemaVerification/SchemaExpectation.cs` | Lee del modelo EF qué tablas y columnas se esperan |
| `Clases/SchemaVerification/SqlSchemaReader.cs` | Lee de `sys.objects` / `sys.columns` qué hay |
| `Clases/SchemaVerification/SchemaComparer.cs` | Compara. Función pura, testeada sin base |
| `Clases/SchemaVerification/SchemaVerificationCommand.cs` | El modo `--verify-schema` y el reporte |
| `Clases/SchemaVerification/ExpectedColumnType.cs` | Una columna cuyo tipo el gate también verifica |
| `Api.Web.FinanceIA.Tests/Clases/SchemaComparerTests.cs` | Cubre la decisión del gate |
| `Api.Web.FinanceIA.Tests/Clases/SchemaColumnTypeTests.cs` | Cubre el chequeo de tipo (`AnalysisEdits.TenantId` antes y después del 081) |
| `Api.Web.FinanceIA.Tests/Clases/SchemaExpectationTests.cs` | Cubre que el modelo real se lea bien |
| `Api.Web.FinanceIA.Tests/Clases/SchemaVerificationCommandTests.cs` | Cubre qué bases se leen con y sin `FINANCIA_SCHEMA_VERIFY_SKIP_DEFAULT`, y los códigos de salida |
