using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ActivaFest.Data;
using ActivaFest.Services.Asistente;
using ActivaFest.Services.Plano;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// LLM falso: "redacta" agregando un prefijo (para comprobar que se marca la respuesta como redactada)
public class RedactorDePrueba : IRedactorLlm
{
    public Task<string?> MejorarAsync(string pregunta, string respuestaBase, CancellationToken ct = default) =>
        Task.FromResult<string?>("✨ " + respuestaBase);

    public Task<EstadoLlm> EstadoAsync(CancellationToken ct = default) =>
        Task.FromResult(new EstadoLlm(true, true, "llama3.2", "http://prueba", true, "Conectado"));
}

// =============================
// 💬 HU14: registro de consultas (métrica del OE3), respaldo y redacción
// =============================
public class HU14AsistenteTests : BaseSprint6
{
    public HU14AsistenteTests(ModeloCompartido modelo) : base(modelo)
    {
    }

    [Fact]
    public async Task Cada_consulta_queda_registrada_sin_guardar_quien_pregunto()
    {
        await Asistente.ResponderAsync(new SolicitudChat { Mensaje = "¿Cuánto cuesta?", EventoId = Rock.Id }, "ana");
        await Asistente.ResponderAsync(new SolicitudChat { Mensaje = "¿Cómo compro entradas?" }, null);
        await Asistente.ResponderAsync(new SolicitudChat { Mensaje = "asdfghjk" }, null);

        var consultas = Db.ConsultasAsistente.OrderBy(c => c.Id).ToList();
        Assert.Equal(3, consultas.Count);
        Assert.Equal((Intenciones.Precio, true, Rock.Id, true), (consultas[0].Intencion, consultas[0].Resuelta, consultas[0].EventoId, consultas[0].ConSesion));
        Assert.Equal((Intenciones.ComoComprar, true, false), (consultas[1].Intencion, consultas[1].Resuelta, consultas[1].ConSesion));
        Assert.Equal((Intenciones.Desconocida, false), (consultas[2].Intencion, consultas[2].Resuelta));
        Assert.Equal("asdfghjk", consultas[2].Pregunta);
        Assert.DoesNotContain(typeof(ActivaFest.Models.ConsultaAsistente).GetProperties(), p => p.Name.Contains("Usuario"));
    }

    [Fact]
    public async Task Las_metricas_miden_el_porcentaje_resuelto_frente_a_la_meta_del_OE3()
    {
        foreach (var m in new[] { "¿Cómo compro entradas?", "¿Qué medios de pago aceptan?", "¿Dónde veo mis entradas?", "hola", "zzzz qqqq" })
            await Asistente.ResponderAsync(new SolicitudChat { Mensaje = m }, null);

        var metricas = await Asistente.MetricasAsync();

        Assert.Equal((5, 4), (metricas.Total, metricas.Resueltas));
        Assert.Equal(0.8, metricas.Tasa!.Value, 3);
        Assert.True(metricas.CumpleMeta);
        Assert.Equal("zzzz qqqq", Assert.Single(metricas.NoResueltas).Pregunta);
        Assert.Equal(5, metricas.PorIntencion.Sum(i => i.Total));
    }

    [Fact]
    public async Task La_respuesta_de_respaldo_ofrece_las_preguntas_frecuentes_y_el_contacto_del_organizador()
    {
        var r = await Asistente.ResponderAsync(new SolicitudChat { Mensaje = "el perro azul del vecino" }, null);

        Assert.False(r.Resuelta);
        Assert.Contains("preguntas frecuentes", r.Texto);
        Assert.Contains("contacto con su organizador", r.Texto);
        Assert.NotEmpty(r.Sugerencias);

        // Sobre un evento concreto, para lo que no está en los datos remite a su organizador por su nombre
        var info = await Asistente.ResponderAsync(new SolicitudChat { Mensaje = "¿Hay estacionamiento?", EventoId = Hamlet.Id }, null);
        Assert.Contains("consulta a su organizador, org,", info.Texto);
    }

    [Fact]
    public async Task Con_Llama_activado_la_respuesta_se_redacta_y_se_cuenta_en_las_metricas()
    {
        var conLlm = new AsistenteService(Db, Modelo.Ml, Catalogo, Recomendaciones, new RedactorDePrueba(), new UbicacionService(Db), NullLogger<AsistenteService>.Instance);

        var r = await conLlm.ResponderAsync(new SolicitudChat { Mensaje = "¿Cuánto cuesta?", EventoId = Rock.Id }, null);

        Assert.True(r.RedactadaPorLlm);
        Assert.StartsWith("✨ ", r.Texto);
        Assert.Contains("S/ 50.00", r.Texto);   // los datos siguen siendo los de la BD
        Assert.Equal(1, (await conLlm.MetricasAsync()).ConLlm);
    }
}

// =============================
// 🦙 HU14: estado de Llama 3.2 (Ollama) para el administrador
// =============================
public class HU14EstadoLlmTests
{
    private static RedactorOllama Redactor(string url) =>
        new(Options.Create(new AsistenteOptions { UsarLlm = true, UrlLlm = url, Modelo = "llama3.2" }), NullLogger<RedactorOllama>.Instance);

    // Servidor HTTP mínimo que responde como Ollama a GET /v1/models
    private static (string Url, Task Servidor) Ollama(string jsonModelos)
    {
        var escucha = new TcpListener(IPAddress.Loopback, 0);
        escucha.Start();
        var puerto = ((IPEndPoint)escucha.LocalEndpoint).Port;
        var servidor = Task.Run(async () =>
        {
            using var cliente = await escucha.AcceptTcpClientAsync();
            using var flujo = cliente.GetStream();
            var buffer = new byte[4096];
            await flujo.ReadAsync(buffer);
            var cuerpo = Encoding.UTF8.GetBytes(jsonModelos);
            var cabecera = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {cuerpo.Length}\r\nConnection: close\r\n\r\n");
            await flujo.WriteAsync(cabecera);
            await flujo.WriteAsync(cuerpo);
            escucha.Stop();
        });
        return ($"http://127.0.0.1:{puerto}/v1", servidor);
    }

    [Fact]
    public async Task Desactivado_lo_indica_y_explica_como_activarlo()
    {
        var estado = await new RedactorDesactivado().EstadoAsync();
        Assert.False(estado.Activado);
        Assert.Contains("UsarLlm", estado.Mensaje);
    }

    [Fact]
    public async Task Detecta_que_Ollama_responde_y_tiene_el_modelo()
    {
        var (url, servidor) = Ollama("""{"object":"list","data":[{"id":"llama3.2:latest","object":"model"}]}""");

        var estado = await Redactor(url).EstadoAsync();
        await servidor;

        Assert.True(estado.Activado && estado.Disponible && estado.ModeloInstalado);
        Assert.StartsWith("Conectado", estado.Mensaje);
    }

    [Fact]
    public async Task Avisa_si_falta_descargar_el_modelo()
    {
        var (url, servidor) = Ollama("""{"object":"list","data":[{"id":"mistral:latest","object":"model"}]}""");

        var estado = await Redactor(url).EstadoAsync();
        await servidor;

        Assert.True(estado.Disponible);
        Assert.False(estado.ModeloInstalado);
        Assert.Contains("ollama pull llama3.2", estado.Mensaje);
    }

    [Fact]
    public async Task Avisa_si_Ollama_no_esta_abierto()
    {
        var libre = new TcpListener(IPAddress.Loopback, 0);
        libre.Start();
        var puerto = ((IPEndPoint)libre.LocalEndpoint).Port;
        libre.Stop();   // nadie escucha en ese puerto

        var estado = await Redactor($"http://127.0.0.1:{puerto}/v1").EstadoAsync();

        Assert.True(estado.Activado);
        Assert.False(estado.Disponible);
        Assert.Contains("no responde", estado.Mensaje);
    }
}

// =============================
// 💬 HU14: página de métricas del asistente (solo Administrador)
// =============================
public class HU14MetricasVistaTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU14MetricasVistaTests(AppDePrueba app)
    {
        _app = app;
    }

    [Fact]
    public async Task El_administrador_ve_el_porcentaje_resuelto_y_el_estado_de_Llama()
    {
        var usuario = _app.Cliente();
        (await usuario.PostAsJsonAsync("/api/chat", new { mensaje = "¿Cómo compro entradas?" })).EnsureSuccessStatusCode();

        var admin = _app.Cliente();
        await IniciarSesionAsync(admin, "admin@test.com", "Admin123!");
        var html = await TextoAsync(await admin.GetAsync("/Analitica/Asistente"));

        Assert.Contains("Asistente", html);
        Assert.Contains("Llama 3.2 (Ollama)", html);
        Assert.Contains("Desactivado", html);
        Assert.Contains("ollama pull llama3.2", html);
        Assert.Contains("ComoComprar", html);
        Assert.Contains("💬 Asistente", await TextoAsync(await admin.GetAsync("/Analitica")));

        using var scope = _app.Services.CreateScope();
        Assert.True(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ConsultasAsistente.Any(c => c.Intencion == "ComoComprar"));
    }

    [Fact]
    public async Task Solo_el_administrador_ve_las_metricas()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: ActivaFest.Models.Roles.Organizador);

        Assert.StartsWith("/Auth/AccesoDenegado", (await organizador.GetAsync("/Analitica/Asistente")).Headers.Location!.PathAndQuery);
        Assert.StartsWith("/Auth/Login", (await _app.Cliente().GetAsync("/Analitica/Asistente")).Headers.Location!.PathAndQuery);
    }
}
