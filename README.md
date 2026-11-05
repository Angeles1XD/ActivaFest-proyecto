# ActivaFest

Plataforma web de difusión, publicidad y venta de entradas geolocalizadas para eventos: catálogo con mapa y ruta, plano del recinto para elegir zona o asiento, compra con QR, panel del organizador, publicidad, analítica del administrador y asistente con IA.

**Stack:** ASP.NET Core 8 MVC (Razor), EF Core + Identity (SQLite en desarrollo), ML.NET, Semantic Kernel con Llama 3.2 (Ollama, opcional), MathNet.Numerics, Chart.js y Leaflet.

---

## 1. Requisitos

| Herramienta | Notas |
|---|---|
| **.NET 8 SDK** | https://dotnet.microsoft.com/download/dotnet/8.0 (Visual Studio 2022 ya lo trae) |
| **Git** | https://git-scm.com |
| **Visual Studio 2022** (17.8 o superior) **o** **VS Code** | En Visual Studio, instala la carga de trabajo **"Desarrollo de ASP.NET y web"**. Visual Studio 2019 no sirve (no soporta .NET 8). |
| **Ollama** (opcional) | Solo para que el asistente use Llama 3.2. Sin Ollama todo funciona igual. |

No hace falta instalar base de datos, Redis, servidor de correo ni Mercado Pago: en desarrollo se usan SQLite, caché en memoria y la **pasarela de pago simulada**.

## 2. Descargar y ejecutar

### Con Visual Studio 2022 (ícono morado)

1. Pantalla de inicio → **Clonar un repositorio** → `https://github.com/Angeles1XD/ActivaFest.git`.
2. Abajo a la derecha, cambia a la rama con la que vas a trabajar (por ejemplo `main`).
3. En el **Explorador de soluciones**: clic derecho en **ActivaFest** → **Establecer como proyecto de inicio**.
4. Junto al botón ▶ elige el perfil **`http`** y pulsa **▶** (F5).
5. Se abre **http://localhost:5054**.

### Con VS Code o la terminal

```bash
git clone https://github.com/Angeles1XD/ActivaFest.git
cd ActivaFest
dotnet run --project ActivaFest --launch-profile http
```

Abre **http://localhost:5054**.

### La primera vez

La aplicación crea sola la base de datos `ActivaFest/app.db` (aplica todas las migraciones) y el administrador:

- **Correo:** `admin@activafest.com`
- **Contraseña:** `Admin123!`

> La base de datos y las fotos subidas (`wwwroot/uploads`) **no se suben a GitHub**: cada integrante tiene sus propios datos locales. Para probar, crea una cuenta de **Organizador**, publica eventos y apruébalos con el administrador.

Cuando alguien sube cambios con migraciones nuevas, basta con hacer **pull** y volver a ejecutar: la base de datos local se actualiza sola, sin perder datos.

## 3. Pruebas

```bash
dotnet test
```

En Visual Studio: **Prueba → Explorador de pruebas → Ejecutar todas**. Son pruebas unitarias y de integración (xUnit + WebApplicationFactory) y no necesitan Ollama.

## 4. Asistente con IA: Llama 3.2 con Ollama (opcional)

El asistente del chat (HU14, HU28) y las consultas a la IA del dashboard (HU24) funcionan **sin** Ollama: entienden la pregunta con ML.NET y un intérprete, y responden con los datos reales de la base de datos. Con Llama 3.2 activado, el modelo redacta las respuestas de forma más natural y elige las funciones de solo lectura (function calling de Semantic Kernel). **Nunca inventa cifras**: si cambia un número, su texto se descarta.

1. Instala Ollama desde https://ollama.com/download y ábrelo.
2. Descarga el modelo:
   ```bash
   ollama pull llama3.2
   ```
3. Crea el archivo **`ActivaFest/appsettings.Development.json`** (no se sube a GitHub) con:
   ```json
   {
     "Asistente": { "UsarLlm": true }
   }
   ```
   En Visual Studio: clic derecho en **ActivaFest** → **Agregar → Nuevo elemento → Archivo JSON**.
4. Ejecuta de nuevo la aplicación.
5. Comprueba el estado como administrador en **Dashboard → 💬 Asistente**:
   - 🟢 **Conectado:** Ollama responde y el modelo está instalado.
   - 🟠 **Activado, pero Ollama no responde** o **falta el modelo:** la página indica qué hacer.
   - ⚪ **Desactivado:** falta el paso 3.

Opciones de `Asistente` (en `appsettings.json`): `UrlLlm` (por defecto `http://localhost:11434/v1`), `Modelo` (`llama3.2`) y `TiempoMaximoSegundos` (6). Si Ollama tarda o falla, el asistente vuelve solo a las respuestas base.

En **Dashboard → 💬 Asistente** también está la métrica del OE3: porcentaje de consultas resueltas sin ayuda humana (meta: 80 %) y las últimas preguntas no resueltas, para ampliar la base de conocimiento (`ActivaFest/Services/Asistente/BaseConocimiento.cs`).

## 5. Configuración opcional (producción)

| Clave | Para qué |
|---|---|
| `ConnectionStrings:DefaultConnection` | Base de datos (SQLite por defecto). |
| `Email:Host`, `Email:User`, `Email:Password` | Envío real de correos (SMTP). Sin configurar, la app funciona y no envía correos. |
| `Pagos:Proveedor` = `MercadoPago` y `Pagos:MercadoPago:AccessToken` | Pagos reales. Por defecto, pasarela simulada (solo en desarrollo). |
| `Qr:Secreto` | Clave para cifrar los QR de las entradas (obligatoria en producción). |
| Variables `REDIS_HOST`, `REDIS_PORT`, `REDIS_PASSWORD` | Redis para caché y bloqueo de aforo y asientos. Sin Redis se usa memoria. |

Los valores secretos van en `appsettings.Development.json`, en variables de entorno (por ejemplo `Email__Host`) o en los secretos del servidor; nunca en `appsettings.json`.

## 6. Estructura

```
ActivaFest/            Aplicación web (MVC)
  Controllers/         Controladores (solo orquestan)
  Services/            Lógica de negocio: compras, plano, analítica, asistente, promociones…
  Models/              Entidades de EF Core y modelos de vista
  Views/               Vistas Razor
  Migrations/          Migraciones de EF Core
  wwwroot/             CSS, JavaScript e imágenes
ActivaFest.Tests/      Pruebas unitarias y de integración
```

## Integrantes

- Milguar
- TafarelM
- Angeles1XD
