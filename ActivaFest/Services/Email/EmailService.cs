using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ActivaFest.Services.Email;

public class EmailSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromEmail { get; set; } = "no-reply@activafest.com";
    public string FromName { get; set; } = "ActivaFest";
    public bool UseStartTls { get; set; } = true;

    public bool Configurado => !string.IsNullOrWhiteSpace(Host);
}

public record AdjuntoCorreo(string NombreArchivo, byte[] Contenido, string TipoContenido = "image/png");

public interface IEmailService
{
    Task EnviarAsync(string destinatario, string asunto, string html, IReadOnlyList<AdjuntoCorreo>? adjuntos = null);
}

public record MensajeCorreo(string Destinatario, string Asunto, string Html, IReadOnlyList<AdjuntoCorreo> Adjuntos);

// Envía un mensaje de inmediato; lanza una excepción si falla (los reintentos los decide la cola)
public interface IEnviadorCorreo
{
    Task EnviarAsync(MensajeCorreo mensaje, CancellationToken ct);
}

// =============================
// ✉️ CORREO TRANSACCIONAL (HU07, HU15)
// =============================
// Envía por SMTP con MailKit (Gmail, SendGrid SMTP relay, Mailtrap, etc.).
// Si no hay SMTP configurado, registra el correo en el log para no romper el flujo en local.
public class SmtpEnviador : IEnviadorCorreo
{
    private readonly EmailSettings _settings;
    private readonly ILogger<SmtpEnviador> _logger;

    public SmtpEnviador(IOptions<EmailSettings> settings, ILogger<SmtpEnviador> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task EnviarAsync(MensajeCorreo correo, CancellationToken ct)
    {
        if (!_settings.Configurado)
        {
            _logger.LogWarning("📧 SMTP no configurado. Correo NO enviado → Para: {Para} | Asunto: {Asunto} | Adjuntos: {Adjuntos}\n{Html}",
                correo.Destinatario, correo.Asunto, correo.Adjuntos.Count, correo.Html);
            return;
        }

        var mensaje = new MimeMessage();
        mensaje.From.Add(new MailboxAddress(_settings.FromName, _settings.FromEmail));
        mensaje.To.Add(MailboxAddress.Parse(correo.Destinatario));
        mensaje.Subject = correo.Asunto;

        var cuerpo = new BodyBuilder { HtmlBody = correo.Html };
        foreach (var adjunto in correo.Adjuntos)
            cuerpo.Attachments.Add(adjunto.NombreArchivo, adjunto.Contenido, ContentType.Parse(adjunto.TipoContenido));
        mensaje.Body = cuerpo.ToMessageBody();

        using var smtp = new SmtpClient { Timeout = 20000 };
        await smtp.ConnectAsync(_settings.Host, _settings.Port,
            _settings.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);

        if (!string.IsNullOrEmpty(_settings.User))
            await smtp.AuthenticateAsync(_settings.User, _settings.Password, ct);

        await smtp.SendAsync(mensaje, ct);
        await smtp.DisconnectAsync(true, ct);

        _logger.LogInformation("📧 Correo enviado a {Para}: {Asunto}", correo.Destinatario, correo.Asunto);
    }
}

// =============================
// 📬 COLA DE CORREOS CON REINTENTOS
// =============================
// Quien envía un correo (compra, registro, moderación) no espera al SMTP: el mensaje se encola y un servicio
// en segundo plano lo envía. Si el SMTP falla se reintenta durante casi 4 minutos, así las entradas
// llegan en menos de 5 minutos aunque el servidor de correo tenga una caída breve (HU07).
public class ColaCorreos : IEmailService
{
    private readonly System.Threading.Channels.Channel<MensajeCorreo> _canal =
        System.Threading.Channels.Channel.CreateUnbounded<MensajeCorreo>(new() { SingleReader = true });

    public System.Threading.Channels.ChannelReader<MensajeCorreo> Lector => _canal.Reader;

    public Task EnviarAsync(string destinatario, string asunto, string html, IReadOnlyList<AdjuntoCorreo>? adjuntos = null) =>
        _canal.Writer.WriteAsync(new MensajeCorreo(destinatario, asunto, html, adjuntos ?? Array.Empty<AdjuntoCorreo>())).AsTask();
}

public class EnvioCorreosService : BackgroundService
{
    // Esperas antes de cada reintento: 10 s + 30 s + 60 s + 120 s = 3 min 40 s
    public static readonly TimeSpan[] EsperasPorDefecto =
        { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120) };

    private const int EnviosSimultaneos = 4;

    private readonly ColaCorreos _cola;
    private readonly IEnviadorCorreo _enviador;
    private readonly ILogger<EnvioCorreosService> _logger;
    private readonly TimeSpan[] _esperas;
    private readonly SemaphoreSlim _cupos = new(EnviosSimultaneos);

    public EnvioCorreosService(ColaCorreos cola, IEnviadorCorreo enviador, ILogger<EnvioCorreosService> logger, TimeSpan[]? esperas = null)
    {
        _cola = cola;
        _enviador = enviador;
        _logger = logger;
        _esperas = esperas ?? EsperasPorDefecto;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var enCurso = new List<Task>();
        await foreach (var mensaje in _cola.Lector.ReadAllAsync(ct))
        {
            await _cupos.WaitAsync(ct);
            enCurso.RemoveAll(t => t.IsCompleted);
            enCurso.Add(Task.Run(async () =>
            {
                try { await EnviarConReintentosAsync(mensaje, ct); }
                finally { _cupos.Release(); }
            }, CancellationToken.None));
        }
        await Task.WhenAll(enCurso);
    }

    // Devuelve true si el correo se envió
    public async Task<bool> EnviarConReintentosAsync(MensajeCorreo mensaje, CancellationToken ct)
    {
        for (var intento = 0; ; intento++)
        {
            try
            {
                await _enviador.EnviarAsync(mensaje, ct);
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (intento >= _esperas.Length)
                {
                    _logger.LogError(ex, "📧 No se pudo enviar el correo a {Para} ({Asunto}) tras {Intentos} intentos",
                        mensaje.Destinatario, mensaje.Asunto, intento + 1);
                    return false;
                }

                _logger.LogWarning("📧 Falló el envío a {Para} ({Motivo}); reintento en {Espera} s",
                    mensaje.Destinatario, ex.GetBaseException().Message, _esperas[intento].TotalSeconds);
                await Task.Delay(_esperas[intento], ct);
            }
        }
    }
}
