using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ActivaFest.Data;
using ActivaFest.Models;
using ActivaFest.Services.Publicidad;
using static ActivaFest.Tests.Formularios;

namespace ActivaFest.Tests;

// =============================
// ⭐ HU20: CATÁLOGO DE PAQUETES PUBLICITARIOS DEL ADMINISTRADOR (pruebas de integración)
// =============================
public class HU20PaquetesTests : IClassFixture<AppDePrueba>
{
    private readonly AppDePrueba _app;

    public HU20PaquetesTests(AppDePrueba app)
    {
        _app = app;
    }

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _app.Cliente();
        await IniciarSesionAsync(admin, "admin@test.com", "Admin123!");
        return admin;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient cliente, string url, params (string, string)[] campos)
    {
        var datos = campos.Select(c => new KeyValuePair<string, string>(c.Item1, c.Item2)).ToList();
        datos.Add(new("__RequestVerificationToken", await TokenAsync(cliente, "/Paquetes")));
        return await cliente.PostAsync(url, new FormUrlEncodedContent(datos));
    }

    private T Con<T>(Func<ApplicationDbContext, T> consulta)
    {
        using var scope = _app.Services.CreateScope();
        return consulta(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private int NuevoPaquete(decimal precio = 30m) => Con(db =>
    {
        var p = new PaqueteDestacado { Nombre = $"Paquete {Guid.NewGuid():N}"[..20], Descripcion = "De prueba", Dias = 5, Precio = precio, Prioridad = 2 };
        db.PaquetesDestacado.Add(p);
        db.SaveChanges();
        return p.Id;
    });

    // Contrato pagado del paquete para un evento del administrador (vigente o ya vencido)
    private async Task<(int EventoId, int ContratoId)> ContratoAsync(int paqueteId, bool vigente)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var admin = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("admin@test.com"))!;
        var evento = new Evento
        {
            Titulo = $"Feria {Guid.NewGuid():N}", Descripcion = "Feria destacada.", CategoriaId = 4, Ubicacion = "Arequipa",
            Fecha = DateTime.Now.AddDays(20), Estado = EstadoEvento.Aprobado, OrganizadorId = admin.Id, Aforo = 10,
            TiposEntrada = { new TipoEntrada { Nombre = "General", Precio = 10, Capacidad = 10 } }
        };
        db.Eventos.Add(evento);
        await db.SaveChangesAsync();

        var paquete = await db.PaquetesDestacado.SingleAsync(p => p.Id == paqueteId);
        var inicio = vigente ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow.AddDays(-10);
        var contrato = new ContratoDestacado
        {
            Codigo = ContratoDestacado.PrefijoCodigo + Guid.NewGuid().ToString("N")[..16], EventoId = evento.Id, PaqueteId = paqueteId,
            OrganizadorId = admin.Id, Precio = paquete.Precio, Dias = paquete.Dias, Prioridad = paquete.Prioridad, Estado = EstadoContrato.Activo,
            FechaPago = inicio, FechaInicio = inicio, FechaFin = inicio.AddDays(paquete.Dias)
        };
        db.ContratosDestacado.Add(contrato);
        await db.SaveChangesAsync();
        return (evento.Id, contrato.Id);
    }

    [Fact]
    public async Task Un_paquete_sin_contrataciones_se_elimina()
    {
        var id = NuevoPaquete();

        await PostAsync(await AdminAsync(), $"/Paquetes/Eliminar/{id}");

        Assert.False(Con(db => db.PaquetesDestacado.Any(p => p.Id == id)));
    }

    // Criterio: un paquete con contrataciones vigentes no se elimina, solo se desactiva
    [Fact]
    public async Task Un_paquete_con_contrataciones_vigentes_no_se_elimina_solo_se_desactiva()
    {
        var id = NuevoPaquete();
        var (eventoId, _) = await ContratoAsync(id, vigente: true);

        await PostAsync(await AdminAsync(), $"/Paquetes/Eliminar/{id}");

        var paquete = Con(db => db.PaquetesDestacado.AsNoTracking().SingleOrDefault(p => p.Id == id));
        Assert.NotNull(paquete);
        Assert.False(paquete.Activo);

        // Lo contratado se respeta: el evento sigue destacado en la portada
        var portada = await _app.Cliente().GetStringAsync("/");
        Assert.Contains($"/Eventos/Detalle/{eventoId}\"", portada);

        // Y ya no se ofrece a los organizadores
        using var scope = _app.Services.CreateScope();
        var destacados = scope.ServiceProvider.GetRequiredService<IDestacadoService>();
        Assert.DoesNotContain(await destacados.PaquetesDisponiblesAsync(), p => p.Id == id);
    }

    [Fact]
    public async Task Con_solo_contrataciones_pasadas_se_desactiva_para_conservar_el_historial()
    {
        var id = NuevoPaquete();
        await ContratoAsync(id, vigente: false);

        await PostAsync(await AdminAsync(), $"/Paquetes/Eliminar/{id}");

        Assert.False(Con(db => db.PaquetesDestacado.AsNoTracking().Single(p => p.Id == id).Activo));
        Assert.True(Con(db => db.ContratosDestacado.Any(c => c.PaqueteId == id)));
    }

    // Criterio: un cambio de precio no afecta a las contrataciones ya pagadas
    [Fact]
    public async Task Un_cambio_de_precio_no_afecta_a_las_contrataciones_ya_pagadas()
    {
        var id = NuevoPaquete(precio: 30m);
        var (eventoId, contratoId) = await ContratoAsync(id, vigente: true);
        var nombre = Con(db => db.PaquetesDestacado.Single(p => p.Id == id).Nombre);

        await PostAsync(await AdminAsync(), $"/Paquetes/Editar/{id}",
            ("Nombre", nombre), ("Descripcion", "Más caro"), ("Dias", "9"), ("Precio", "99.90"), ("Prioridad", "5"), ("Activo", "true"));

        var pagado = Con(db => db.ContratosDestacado.AsNoTracking().Single(c => c.Id == contratoId));
        Assert.Equal((30m, 5, 2), (pagado.Precio, pagado.Dias, pagado.Prioridad));
        Assert.Equal(99.90m, Con(db => db.PaquetesDestacado.Single(p => p.Id == id).Precio));

        // Una contratación nueva sí usa el precio actualizado
        using var scope = _app.Services.CreateScope();
        var admin = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("admin@test.com"))!;
        var nuevo = await scope.ServiceProvider.GetRequiredService<IDestacadoService>().ContratarAsync(admin.Id, true, eventoId, id);
        Assert.Equal(99.90m, nuevo.Contrato!.Precio);
    }

    [Fact]
    public async Task Un_paquete_desactivado_no_se_puede_contratar()
    {
        var id = NuevoPaquete();
        var (eventoId, _) = await ContratoAsync(NuevoPaquete(), vigente: false);
        await PostAsync(await AdminAsync(), $"/Paquetes/Editar/{id}",
            ("Nombre", "Inactivo"), ("Descripcion", "x"), ("Dias", "5"), ("Precio", "30"), ("Prioridad", "1"), ("Activo", "false"));

        using var scope = _app.Services.CreateScope();
        var admin = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("admin@test.com"))!;
        var r = await scope.ServiceProvider.GetRequiredService<IDestacadoService>().ContratarAsync(admin.Id, true, eventoId, id);

        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Solo_el_administrador_gestiona_los_paquetes()
    {
        var organizador = _app.Cliente();
        await RegistrarAsync(organizador, Correo(), rol: Roles.Organizador);
        var id = NuevoPaquete();

        Assert.StartsWith("/Auth/AccesoDenegado", (await organizador.GetAsync("/Paquetes")).Headers.Location!.PathAndQuery);
        var token = await TokenAsync(organizador, "/Eventos/Crear");
        await organizador.PostAsync($"/Paquetes/Eliminar/{id}", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        Assert.True(Con(db => db.PaquetesDestacado.Any(p => p.Id == id)));
    }
}
