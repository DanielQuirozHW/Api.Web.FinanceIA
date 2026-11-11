# Co-branding de los informes PDF

Los dos informes PDF (empresa: "Informe crediticio"; persona humana: "Evaluación crediticia") pueden llevar
el logo del cliente en el encabezado de la página 1. El pie **siempre** queda como hoy:
"Elaborado con Financia · Confidencial · ID …" (solo texto: el símbolo no va en el pie, porque a ese tamaño
queda por debajo del mínimo del manual de marca).

Sin un logo utilizable, el PDF sale **idéntico byte a byte** al de hoy: símbolo + "Financia" en el encabezado.

## Las tres claves

Son filas comunes de `[dbo].[TenantSettings]` (la tabla de `034_tenant_settings.sql`). **No hace falta DDL.**
Las constantes están en `Models/Financia/TenantSettingKeys.cs`.

| Clave | Qué hace | Obligatoria |
|---|---|---|
| `branding.logo_url` | URL https pública del logo. **Es lo único que activa el co-branding.** | Sí, para co-brandear |
| `branding.company_name` | Nombre que se imprime al lado del logo (Sora Bold). | No |
| `branding.accent_color` | Color `#RRGGBB` que reemplaza el azul de Financia: la franja superior, las etiquetas en acento, los acentos de los gráficos y su tinte claro. | No |

Reglas:

- `branding.company_name` **solo no cambia el encabezado.** Varios clientes ya lo tienen cargado (autor del PDF y
  `/auth/me`), y con este cambio no les cambia nada.
- `branding.accent_color` **solo se aplica junto con un logo utilizable.** Sin logo, el PDF es 100 % Financia y
  se escribe una línea Information que lo avisa.
- El autor del PDF sigue siendo `branding.company_name` (o "Financia"), y el creador sigue siendo "Financia".
- `branding.logo_url` es la misma clave que usan los emails. Si ya está cargada para los emails, los PDF la
  toman también (si cumple los requisitos de abajo).

## Requisitos del logo

El servidor descarga el logo él mismo, así que la URL pasa por controles de seguridad (anti-SSRF):

- **https**, puerto **443**, sin usuario ni contraseña en la URL.
- El host tiene que resolver a una IP **pública**. Se rechazan las IP privadas, de loopback, link-local
  (incluida la de metadata 169.254.169.254), CGNAT y sus equivalentes IPv6.
- **Sin redirecciones.** La URL tiene que ser la final: un 301/302 se rechaza.
- Pesa **≤ 1 MB**.
- Es **PNG o JPEG**. Se decide por el contenido del archivo, no por la extensión ni por el Content-Type.
  SVG, GIF y WebP se rechazan.
- Mide **≤ 4000 × 4000 px**.
- Responde en **≤ 5 segundos**.

Recomendado:

- **Fondo transparente** (PNG).
- **Formato apaisado**, por ejemplo 4:1 (480 × 120 px). El logo se ajusta, sin deformarse, a una caja de 28 pt
  de alto y hasta 150 pt de ancho. Un logo cuadrado queda chico; uno muy apaisado queda finito.
- En el **tema oscuro** el logo va sobre una placa blanca redondeada, así que un logo con tinta oscura se sigue
  viendo.

## El color de acento

- El formato es exactamente `#RRGGBB` (por ejemplo `#1B4F9C`). Cualquier otra cosa se ignora y queda un Warning.
- Tiene que contrastar al menos **4.5:1** (WCAG AA) contra el papel y contra las superficies del tema que se
  exporta. Se evalúa **por tema**:
  - Un color oscuro (por ejemplo `#1B4F9C`) pasa en el tema claro y se ignora en el oscuro.
  - Un color claro (por ejemplo `#FFD400`) pasa en el tema oscuro y se ignora en el claro.
  - Un mismo color rara vez sirve para los dos temas, y eso es normal: donde no pasa, queda el azul de Financia.

## Cómo cargarlo

Reemplazar `<TENANT_SLUG>` por el slug del tenant y la URL por la real:

```sql
INSERT INTO [dbo].[TenantSettings] ([TenantId], [SettingKey], [SettingValue], [Description])
VALUES (N'<TENANT_SLUG>', N'branding.logo_url',     N'https://cdn.example.com/logos/cliente.png', N'Logo del encabezado de los PDF y de los emails'),
       (N'<TENANT_SLUG>', N'branding.company_name', N'SGR Ejemplo',                              N'Nombre al lado del logo'),
       (N'<TENANT_SLUG>', N'branding.accent_color', N'#1B4F9C',                                  N'Acento del PDF (opcional)');
```

Si la fila ya existe (hay una sola por tenant y clave, `UQ_TenantSettings_Tenant_Key`), se actualiza:

```sql
UPDATE [dbo].[TenantSettings]
SET    [SettingValue] = N'https://cdn.example.com/logos/cliente-v2.png'
WHERE  [TenantId] = N'<TENANT_SLUG>' AND [SettingKey] = N'branding.logo_url';
```

Para volver al encabezado Financia, se borra la fila `branding.logo_url` (o se deja en blanco). Ojo: los
emails también dejan de llevar el logo.

## Cómo verificarlo

1. Exportar un PDF de ese tenant desde el Portal y mirar el encabezado de la página 1: tiene que estar el logo
   del cliente (y el nombre, si se cargó).
2. Si sigue saliendo "Financia", el logo fue rechazado. En Application Insights queda un Warning con el motivo:

```kusto
traces
| where timestamp > ago(1d) and severityLevel >= 1
| where message has "branding" or message has "PDF branding"
| extend TenantId = tostring(customDimensions.TenantId),
         Reason   = tostring(customDimensions.Reason),
         Host     = tostring(customDimensions.LogoHost)
| project timestamp, TenantId, Reason, Host, message
| order by timestamp desc
```

La línea del export también lo dice: `PDF exported … brand=client` (co-brandeado) o `brand=financia`.

Motivos posibles (`Reason`):

| Motivo | Qué significa |
|---|---|
| `scheme` | La URL no es https. |
| `port` | La URL tiene un puerto distinto de 443. |
| `userinfo` | La URL tiene usuario o contraseña. |
| `blocked_address` | El host resuelve a una IP no pública. |
| `redirect` | El servidor respondió una redirección. |
| `http_status` | El servidor respondió 4xx/5xx. |
| `oversize` | Pesa más de 1 MB. |
| `format` | No es PNG ni JPEG (SVG, GIF, WebP, HTML…). |
| `dimensions` | Mide más de 4000 px de lado, o tiene un lado en 0. |
| `undecodable` | Parece PNG/JPEG pero el archivo está roto. |
| `timeout` | Tardó más de 5 s. |
| `network` | Falló DNS, TCP o TLS. |

Por diseño, el log nombra solo el **host** de la URL, nunca la ruta.

## Cuándo se ven los cambios (caché)

- Las tres claves se leen en **cada export**. Cargar, cambiar o borrar una clave (incluido **cambiar la URL**)
  se ve en el próximo PDF.
- La **imagen** se cachea por (tenant, URL) en cada instancia del App Service: **6 horas** si se aceptó y
  **15 minutos** si se rechazó (así una URL rota no le cuesta 5 s a cada export). Por eso:
  - **Reemplazar el archivo en la misma URL** puede tardar **hasta 6 h** en verse, o hasta reiniciar el App
    Service. Lo más rápido es publicar el logo nuevo en otra URL (por ejemplo `logo-v2.png`) y actualizar la fila.
  - Después de **arreglar un logo rechazado** en la misma URL, se reintenta a los 15 minutos (o al reiniciar).
- La caché es por instancia: con varias instancias, cada una descarga el logo una vez.
