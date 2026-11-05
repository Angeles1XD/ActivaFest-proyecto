# ActivaFest

**Sistema web de gestión y venta de entradas para eventos**

ActivaFest es una plataforma web desarrollada con ASP.NET Core 8 MVC que permite gestionar, promocionar y comercializar entradas para diferentes eventos. Integra herramientas de geolocalización, selección de asientos, generación de entradas con códigos QR, análisis de ventas y un asistente basado en inteligencia artificial.

## 1. Funcionalidades principales

- Registro, autenticación y gestión de usuarios.
- Publicación, búsqueda y administración de eventos.
- Visualización de eventos mediante mapas interactivos.
- Selección de zonas y asientos.
- Compra de entradas y generación de códigos QR.
- Validación de entradas y control de aforo.
- Gestión de organizadores, promociones y publicidad.
- Panel administrativo con estadísticas y reportes.
- Asistente inteligente para consultas y recomendaciones.

## 2. Tecnologías utilizadas

| Tecnología | Aplicación |
|---|---|
| ASP.NET Core 8 MVC | Desarrollo de la aplicación web |
| C# | Programación del sistema |
| Entity Framework Core | Acceso y gestión de datos |
| SQLite | Base de datos de desarrollo |
| HTML, CSS y JavaScript | Interfaz de usuario |
| Bootstrap | Diseño adaptable |
| Leaflet | Mapas y geolocalización |
| Chart.js | Gráficos y estadísticas |
| ML.NET y Semantic Kernel | Funcionalidades de inteligencia artificial |

## 3. Instalación y ejecución

**Requisitos:**
- .NET 8 SDK.
- Visual Studio 2022 o Visual Studio Code.
- Git.

**Clonar el repositorio:**

```bash
git clone https://github.com/Angeles1XD/ActivaFest-proyecto.git
```

**Ingresar a la carpeta:**

```bash
cd ActivaFest-proyecto
```

**Ejecutar la aplicación:**

```bash
dotnet restore
dotnet run --project ActivaFest/ActivaFest.csproj --launch-profile http
```

**Acceder desde el navegador:**

http://localhost:5054

La aplicación utiliza SQLite en desarrollo y dispone de una pasarela de pagos simulada para realizar pruebas.

## 4. Pruebas del sistema

El proyecto incluye pruebas unitarias y de integración para verificar las funcionalidades implementadas.

Para ejecutar las pruebas:

```bash
dotnet test
```

## 5. Integrantes

- Milguar Esmith Arestegui Parra
- Tafarel Aguinaga Mamani Acuña
- Angeles Pinto Villavicencio

## 6. Repositorio

**GitHub:** https://github.com/Angeles1XD/ActivaFest-proyecto

**Rama principal:** main
