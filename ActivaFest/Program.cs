using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services;
using ActivaFest.Services.Catalogo;
using ActivaFest.Services.Compras;
using ActivaFest.Services.Email;
using ActivaFest.Services.Entradas;
using ActivaFest.Services.Imagenes;
using ActivaFest.Services.Pagos;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// =============================
// 🔥 BASE DE DATOS SQLITE
// =============================
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=app.db";

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));

// =============================
// 🔐 IDENTITY: USUARIOS Y ROLES (HU01)
// =============================
// Las contraseñas se guardan con hash PBKDF2 (PasswordHasher de ASP.NET Core Identity)
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = true;
        options.Password.RequireDigit = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager<SignInManagerActivaFest>()
    .AddDefaultTokenProviders();

// HU18: el enlace de recuperación vence en 1 hora (Identity usa 1 día por defecto).
// Es de un solo uso: al cambiar la contraseña se renueva el SecurityStamp y el token deja de ser válido.
builder.Services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromHours(1));

// HU19: al desactivar una cuenta o cambiar sus roles se renueva su SecurityStamp; las sesiones abiertas
// se revalidan cada minuto (por defecto cada 30), así el cambio aplica casi de inmediato
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Auth/Login";
    options.LogoutPath = "/Auth/Logout";
    options.AccessDeniedPath = "/Auth/AccesoDenegado";
    options.Cookie.Name = "ActivaFest.Auth";
    options.Cookie.HttpOnly = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
});

// =============================
// ✉️ CORREO (HU15)
// =============================
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("Email"));
// Los correos se encolan y se envían en segundo plano con reintentos (las entradas llegan en < 5 min, HU07)
builder.Services.AddSingleton<IEnviadorCorreo, SmtpEnviador>();
builder.Services.AddSingleton<ColaCorreos>();
builder.Services.AddSingleton<IEmailService>(sp => sp.GetRequiredService<ColaCorreos>());
builder.Services.AddHostedService<EnvioCorreosService>();

// =============================
// 📚 CATÁLOGO E IMÁGENES (HU02, HU03, HU05)
// =============================
builder.Services.AddScoped<ICatalogoService, CatalogoService>();
// Imágenes: disco local en desarrollo o Cloudinary (capa gratuita) cuando hay credenciales (HU09)
builder.Services.Configure<ImagenesOptions>(builder.Configuration.GetSection("Imagenes"));
builder.Services.AddSingleton<AlmacenamientoLocalImagenes>();
builder.Services.AddHttpClient<CloudinaryAlmacenamiento>(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddScoped<IAlmacenamientoImagenes>(sp =>
{
    var imagenes = sp.GetRequiredService<IOptions<ImagenesOptions>>().Value;
    return imagenes.Proveedor == "Cloudinary" && imagenes.Cloudinary.Configurado
        ? sp.GetRequiredService<CloudinaryAlmacenamiento>()
        : sp.GetRequiredService<AlmacenamientoLocalImagenes>();
});

// =============================
// 🧑‍💼 ORGANIZADOR Y PUERTA (HU08, HU09, HU13)
// =============================
builder.Services.AddScoped<ActivaFest.Services.Eventos.IEventoEdicionService, ActivaFest.Services.Eventos.EventoEdicionService>();
builder.Services.AddScoped<ActivaFest.Services.Eventos.ICancelacionEventoService, ActivaFest.Services.Eventos.CancelacionEventoService>();
builder.Services.AddScoped<ActivaFest.Services.Acceso.IAccesoService, ActivaFest.Services.Acceso.AccesoService>();
builder.Services.AddScoped<ActivaFest.Services.Panel.IPanelService, ActivaFest.Services.Panel.PanelService>();

// =============================
// ⭐ PUBLICIDAD Y 💰 COMISIONES (HU12, HU16)
// =============================
builder.Services.Configure<ComisionesOptions>(builder.Configuration.GetSection("Comisiones"));
builder.Services.AddScoped<ActivaFest.Services.Publicidad.IDestacadoService, ActivaFest.Services.Publicidad.DestacadoService>();
builder.Services.AddScoped<ActivaFest.Services.Comisiones.IComisionService, ActivaFest.Services.Comisiones.ComisionService>();

// 📊 Analítica del administrador (HU21, HU22, HU29)
builder.Services.AddScoped<ActivaFest.Services.Analitica.IAnaliticaService, ActivaFest.Services.Analitica.AnaliticaService>();
builder.Services.AddScoped<ActivaFest.Services.Analitica.IAsistenteAnaliticoService, ActivaFest.Services.Analitica.AsistenteAnaliticoService>();
builder.Services.AddScoped<ActivaFest.Services.Organizadores.ISolicitudOrganizadorService, ActivaFest.Services.Organizadores.SolicitudOrganizadorService>();
builder.Services.AddScoped<ActivaFest.Services.Organizadores.ISolicitudPersonalService, ActivaFest.Services.Organizadores.SolicitudPersonalService>();
builder.Services.Configure<ActivaFest.Controllers.ContactoOptions>(builder.Configuration.GetSection("Contacto"));
builder.Services.AddHostedService<ActivaFest.Services.Organizadores.VencimientoOrganizadoresService>();
builder.Services.AddScoped<ActivaFest.Services.Acceso.IReputacionPersonalService, ActivaFest.Services.Acceso.ReputacionPersonalService>();
builder.Services.AddHostedService<ActivaFest.Services.Organizadores.AutomatizacionPersonalService>();
builder.Services.Configure<ActivaFest.Models.EventosGratuitosOptions>(builder.Configuration.GetSection("EventosGratuitos"));
builder.Services.AddScoped<ActivaFest.Services.Eventos.IEventoGratuitoService, ActivaFest.Services.Eventos.EventoGratuitoService>();

// =============================
// 🛒 COMPRAS, PAGOS Y ENTRADAS QR (HU06, HU07, HU11)
// =============================
builder.Services.Configure<ComprasOptions>(builder.Configuration.GetSection("Compras"));
builder.Services.Configure<PagosOptions>(builder.Configuration.GetSection("Pagos"));
builder.Services.Configure<QrOptions>(builder.Configuration.GetSection("Qr"));

builder.Services.AddSingleton<IQrEntradaService, QrEntradaService>();
builder.Services.AddSingleton<BloqueoEnMemoria>();
builder.Services.AddSingleton<BloqueoAsientosEnMemoria>();
builder.Services.AddScoped<ICompraService, CompraService>();
builder.Services.AddScoped<ICarritoService, CarritoService>();
builder.Services.AddScoped<ActivaFest.Services.Promociones.IPromocionService, ActivaFest.Services.Promociones.PromocionService>();
builder.Services.AddHostedService<ExpiracionReservasService>();

builder.Services.AddHttpClient<MercadoPagoPasarela>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddScoped<PasarelaSimulada>();
builder.Services.AddScoped<IPasarelaPago>(sp =>
{
    var pagos = sp.GetRequiredService<IOptions<PagosOptions>>().Value;

    if (pagos.Proveedor == MercadoPagoPasarela.NombreProveedor)
    {
        var mercadoPago = sp.GetRequiredService<MercadoPagoPasarela>();
        if (mercadoPago.Disponible)
            return mercadoPago;
    }

    // La simulada nunca queda activa en producción por accidente
    var permitirSimulado = sp.GetRequiredService<IWebHostEnvironment>().IsDevelopment() || pagos.PermitirSimuladoFueraDeDesarrollo;
    return permitirSimulado ? sp.GetRequiredService<PasarelaSimulada>() : new PasarelaNoDisponible();
});

// =============================
// 🔥 MVC
// =============================
builder.Services.AddControllersWithViews(ActivaFest.Models.MensajesValidacionEnEspanol.Configurar);

// =============================
// 🔥 CACHE EN MEMORIA (para tu Index)
// =============================
builder.Services.AddMemoryCache();

// =============================
// 🔥 SESIONES (necesitan cache distribuido)
// =============================
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(20); // tiempo de sesión
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});
builder.Services.AddHttpContextAccessor();

// =============================
// 🤖 ASISTENTE VIRTUAL Y 🎯 RECOMENDACIONES (HU14, HU17)
// =============================
builder.Services.AddSingleton<MachineLearningService>();
builder.Services.Configure<ActivaFest.Services.Asistente.AsistenteOptions>(builder.Configuration.GetSection("Asistente"));
builder.Services.AddScoped<ActivaFest.Services.Plano.IUbicacionService, ActivaFest.Services.Plano.UbicacionService>();
builder.Services.AddScoped<ActivaFest.Services.Asistente.IAsistenteService, ActivaFest.Services.Asistente.AsistenteService>();
builder.Services.AddScoped<ActivaFest.Services.Recomendaciones.IRecomendacionService, ActivaFest.Services.Recomendaciones.RecomendacionService>();

// El LLM (Ollama) solo se usa si está habilitado: en Render no hay Ollama y el asistente funciona igual
builder.Services.AddSingleton<ActivaFest.Services.Asistente.IRedactorLlm>(sp =>
    sp.GetRequiredService<IOptions<ActivaFest.Services.Asistente.AsistenteOptions>>().Value.UsarLlm
        ? ActivatorUtilities.CreateInstance<ActivaFest.Services.Asistente.RedactorOllama>(sp)
        : new ActivaFest.Services.Asistente.RedactorDesactivado(sp.GetRequiredService<IOptions<ActivaFest.Services.Asistente.AsistenteOptions>>()));

// 🧠 Agente de IA del asistente (chat y voz, por rol); si el modelo no responde, contesta el asistente base
builder.Services.Configure<ActivaFest.Services.Asistente.AgenteIAOptions>(builder.Configuration.GetSection("AsistenteIA"));
builder.Services.AddScoped<ActivaFest.Services.Asistente.IAgenteIA, ActivaFest.Services.Asistente.AgenteIA>();

// ⚡ Acciones del asistente por rol: reservas y avisos (usuario), publicaciones y dashboard de ventas (organizador)
builder.Services.AddScoped<ActivaFest.Services.Asistente.IAccionesAsistente, ActivaFest.Services.Asistente.AccionesAsistente>();
builder.Services.AddHostedService<ActivaFest.Services.Asistente.RecordatoriosEventosService>();

// 🔔 Panel de notificaciones (campana): recibe los recordatorios y alertas que se piden al asistente
builder.Services.AddScoped<ActivaFest.Services.Notificaciones.INotificacionService, ActivaFest.Services.Notificaciones.NotificacionService>();

// Límite de mensajes al chat por IP: evita abusos del asistente
builder.Services.AddRateLimiter(opciones =>
{
    opciones.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    opciones.AddPolicy(ActivaFest.Controllers.ChatController.PoliticaLimite, contexto =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            contexto.Connection.RemoteIpAddress?.ToString() ?? "anonimo",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
});

// =============================
// 🔥 REDIS (opcional, no romper en local)
// =============================
var redisHost = Environment.GetEnvironmentVariable("REDIS_HOST");
var redisPort = Environment.GetEnvironmentVariable("REDIS_PORT");
var redisPassword = Environment.GetEnvironmentVariable("REDIS_PASSWORD");

if (!string.IsNullOrEmpty(redisHost))
{
    // Redis con respaldo en memoria: si Redis cae, el sitio sigue funcionando (ver CacheResiliente)
    var redisOptions = new Microsoft.Extensions.Caching.StackExchangeRedis.RedisCacheOptions
    {
        // abortConnect=false: no bloquea el arranque; timeouts cortos para no colgar las páginas si Redis no responde
        Configuration = $"{redisHost}:{redisPort},password={redisPassword},abortConnect=false,connectTimeout=3000,syncTimeout=3000,asyncTimeout=3000",
        InstanceName = "activafest:"
    };

    builder.Services.AddSingleton<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(sp => new CacheResiliente(
        new Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache(Options.Create(redisOptions)),
        new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions())),
        sp.GetRequiredService<ILogger<CacheResiliente>>()));

    // HU11: bloqueo distribuido de aforo en el mismo Redis (con respaldo local si no responde)
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
        StackExchange.Redis.ConnectionMultiplexer.Connect(redisOptions.Configuration));
    builder.Services.AddSingleton<IBloqueoDistribuido, BloqueoRedis>();
    builder.Services.AddSingleton<IBloqueoAsientos, BloqueoAsientosRedis>(); // HU26: asientos apartados 10 min
}
else
{
    builder.Services.AddDistributedMemoryCache(); // local
    builder.Services.AddSingleton<IBloqueoDistribuido>(sp => sp.GetRequiredService<BloqueoEnMemoria>());
    builder.Services.AddSingleton<IBloqueoAsientos>(sp => sp.GetRequiredService<BloqueoAsientosEnMemoria>());
}

var app = builder.Build();

// Migraciones BD + roles y administrador inicial
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();

    await DbInitializer.SeedAsync(scope.ServiceProvider, app.Configuration, app.Logger);
    await DbInitializer.ReubicarAsientosAsync(db, app.Logger);

    // Falla al arrancar (y no al primer pago) si falta la clave para firmar los QR en producción
    scope.ServiceProvider.GetRequiredService<IQrEntradaService>();

    var pasarela = scope.ServiceProvider.GetRequiredService<IPasarelaPago>();
    app.Logger.LogInformation("💳 Pasarela de pago activa: {Pasarela}", pasarela.Nombre);
}

// =============================
// 🔥 PIPELINE
// =============================
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// Fechas en español y montos en soles (es-PE)
var culturaPeru = new CultureInfo("es-PE");
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(culturaPeru),
    SupportedCultures = new[] { culturaPeru },
    SupportedUICultures = new[] { culturaPeru }
});

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// 🔥 SESIÓN (antes de endpoints)
app.UseSession();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// =============================
// 🔥 RUTA PRINCIPAL
// =============================
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// El asistente virtual (/api/chat) está en ChatController

app.Run();

// Expone Program a las pruebas de integración (WebApplicationFactory)
public partial class Program { }
