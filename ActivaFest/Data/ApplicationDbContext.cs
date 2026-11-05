using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ActivaFest.Models;

namespace ActivaFest.Data
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<Evento> Eventos { get; set; }
        public DbSet<Categoria> Categorias { get; set; }
        public DbSet<TipoEntrada> TiposEntrada { get; set; }
        public DbSet<EventoImagen> EventoImagenes { get; set; }
        public DbSet<Orden> Ordenes { get; set; }
        public DbSet<OrdenItem> OrdenItems { get; set; }
        public DbSet<Entrada> Entradas { get; set; }
        public DbSet<PaqueteDestacado> PaquetesDestacado { get; set; }
        public DbSet<ContratoDestacado> ContratosDestacado { get; set; }
        public DbSet<Liquidacion> Liquidaciones { get; set; }
        public DbSet<HistorialBusqueda> HistorialBusquedas { get; set; }

        public DbSet<VisualizacionEvento> VisualizacionesEvento { get; set; }

        public DbSet<AsignacionAcceso> AsignacionesAcceso { get; set; }

        public DbSet<Asiento> Asientos { get; set; }

        public DbSet<AuditoriaAnalitica> AuditoriasAnalitica { get; set; }

        // HU30: promociones y cupones personales
        public DbSet<Promocion> Promociones { get; set; }
        public DbSet<Cupon> Cupones { get; set; }

        // HU23: umbrales del semáforo de variabilidad (una fila)
        public DbSet<UmbralesVariabilidad> UmbralesVariabilidad { get; set; }

        // HU14: consultas al asistente (métrica del OE3)
        public DbSet<ConsultaAsistente> ConsultasAsistente { get; set; }

        // HU31 y HU32: solicitudes al administrador (habilitar organizador y personal de control)
        public DbSet<SolicitudOrganizador> SolicitudesOrganizador { get; set; }
        public DbSet<SolicitudPersonal> SolicitudesPersonal { get; set; }
        public DbSet<CalificacionPersonal> CalificacionesPersonal { get; set; }
        public DbSet<PagoPublicacion> PagosPublicacion { get; set; }
        public DbSet<InteresEvento> InteresesEvento { get; set; }

        // Avisos de eventos próximos que el usuario pidió al asistente virtual
        public DbSet<RecordatorioEvento> RecordatoriosEvento { get; set; }

        // Panel de notificaciones y alertas por rol que vigila el asistente
        public DbSet<Notificacion> Notificaciones { get; set; }
        public DbSet<AlertaAsistente> AlertasAsistente { get; set; }

        // Paquetes publicitarios iniciales (el administrador puede editarlos)
        public static readonly PaqueteDestacado[] PaquetesIniciales =
        {
            new() { Id = 1, Nombre = "Impulso", Descripcion = "Tu evento en la sección Destacados de la portada durante 3 días.", Dias = 3, Precio = 29.90m, Prioridad = 1 },
            new() { Id = 2, Nombre = "Plus", Descripcion = "Una semana en Destacados, por delante de los paquetes Impulso.", Dias = 7, Precio = 59.90m, Prioridad = 2 },
            new() { Id = 3, Nombre = "Premium", Descripcion = "Dos semanas en los primeros lugares de Destacados.", Dias = 15, Precio = 109.90m, Prioridad = 3 }
        };

        // Categorías iniciales del catálogo (RF02)
        public static readonly Categoria[] CategoriasIniciales =
        {
            new() { Id = 1, Nombre = "Conciertos", Icono = "🎵", Color = "#a855f7", Imagen = "/img/categorias/conciertos.jpg" },
            new() { Id = 2, Nombre = "Teatro", Icono = "🎭", Color = "#ec4899", Imagen = "/img/categorias/teatro.jpg" },
            new() { Id = 3, Nombre = "Festivales", Icono = "🎉", Color = "#f97316", Imagen = "/img/categorias/festivales.jpg" },
            new() { Id = 4, Nombre = "Ferias", Icono = "🎪", Color = "#f59e0b", Imagen = "/img/categorias/ferias.jpg" },
            new() { Id = 5, Nombre = "Exposiciones", Icono = "🖼️", Color = "#06b6d4", Imagen = "/img/categorias/exposiciones.jpg" },
            new() { Id = 6, Nombre = "Deportes", Icono = "⚽", Color = "#22c55e", Imagen = "/img/categorias/deportes.jpg" },
            new() { Id = 7, Nombre = "Gastronomía", Icono = "🍽️", Color = "#ef4444", Imagen = "/img/categorias/gastronomia.jpg" },
            new() { Id = 8, Nombre = "Familiar", Icono = "👨‍👩‍👧", Color = "#14b8a6", Imagen = "/img/categorias/familiar.jpg" },
            new() { Id = 9, Nombre = "Cultura", Icono = "🏛️", Color = "#3b82f6", Imagen = "/img/categorias/cultura.jpg" },
            new() { Id = 10, Nombre = "Otros", Icono = "🎫", Color = "#8b5cf6", Imagen = "/img/categorias/otros.jpg" }
        };

        public const int CategoriaOtrosId = 10;

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<Categoria>(c =>
            {
                c.HasIndex(x => x.Nombre).IsUnique();
                c.HasData(CategoriasIniciales);
            });

            builder.Entity<Evento>(e =>
            {
                e.HasIndex(x => x.Estado);
                e.HasIndex(x => x.Fecha);

                e.HasOne(x => x.Organizador)
                    .WithMany()
                    .HasForeignKey(x => x.OrganizadorId)
                    .OnDelete(DeleteBehavior.SetNull);

                e.HasOne(x => x.Categoria)
                    .WithMany(c => c.Eventos)
                    .HasForeignKey(x => x.CategoriaId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<TipoEntrada>(t =>
            {
                t.Property(x => x.Precio).HasPrecision(10, 2);
                t.Property(x => x.RowVersion).IsConcurrencyToken();

                t.HasOne(x => x.Evento)
                    .WithMany(e => e.TiposEntrada)
                    .HasForeignKey(x => x.EventoId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<EventoImagen>(i =>
            {
                i.HasOne(x => x.Evento)
                    .WithMany(e => e.Imagenes)
                    .HasForeignKey(x => x.EventoId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // =============================
            // 🧾 COMPRAS Y ENTRADAS (SPRINT 3)
            // =============================
            builder.Entity<Orden>(o =>
            {
                o.HasIndex(x => x.Codigo).IsUnique();
                o.HasIndex(x => new { x.Estado, x.FechaExpiracion });
                o.HasIndex(x => x.UsuarioId);
                o.Property(x => x.Total).HasPrecision(10, 2);

                o.HasOne(x => x.Usuario).WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Restrict);
                // Un evento con ventas no se puede borrar en cascada
                o.HasOne(x => x.Evento).WithMany().HasForeignKey(x => x.EventoId).OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<OrdenItem>(i =>
            {
                i.Property(x => x.PrecioUnitario).HasPrecision(10, 2);
                i.HasOne(x => x.Orden).WithMany(o => o.Items).HasForeignKey(x => x.OrdenId).OnDelete(DeleteBehavior.Cascade);
                i.HasOne(x => x.TipoEntrada).WithMany().HasForeignKey(x => x.TipoEntradaId).OnDelete(DeleteBehavior.Restrict);
                i.HasOne(x => x.Asiento).WithMany().HasForeignKey(x => x.AsientoId).OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<Entrada>(e =>
            {
                e.HasIndex(x => x.Codigo).IsUnique();
                e.HasIndex(x => x.UsuarioId);
                e.HasOne(x => x.Orden).WithMany(o => o.Entradas).HasForeignKey(x => x.OrdenId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne(x => x.Evento).WithMany().HasForeignKey(x => x.EventoId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(x => x.TipoEntrada).WithMany().HasForeignKey(x => x.TipoEntradaId).OnDelete(DeleteBehavior.Restrict);

                // HU26: un asiento nunca está en dos entradas (índice único: imposible la doble venta)
                e.HasIndex(x => x.AsientoId).IsUnique().HasFilter("AsientoId IS NOT NULL");
                e.HasOne(x => x.Asiento).WithMany().HasForeignKey(x => x.AsientoId).OnDelete(DeleteBehavior.Restrict);
            });

            // =============================
            // ⭐ PUBLICIDAD Y 💸 LIQUIDACIONES (SPRINT 5)
            // =============================
            builder.Entity<Orden>(o =>
            {
                o.Property(x => x.Comision).HasPrecision(10, 2);
                o.Property(x => x.PorcentajeComision).HasPrecision(5, 2);
            });

            builder.Entity<PaqueteDestacado>(p =>
            {
                p.Property(x => x.Precio).HasPrecision(10, 2);
                p.HasData(PaquetesIniciales);
            });

            builder.Entity<ContratoDestacado>(c =>
            {
                c.HasIndex(x => x.Codigo).IsUnique();
                c.HasIndex(x => new { x.Estado, x.FechaFin });
                c.Property(x => x.Precio).HasPrecision(10, 2);
                c.HasOne(x => x.Evento).WithMany().HasForeignKey(x => x.EventoId).OnDelete(DeleteBehavior.Restrict);
                c.HasOne(x => x.Paquete).WithMany().HasForeignKey(x => x.PaqueteId).OnDelete(DeleteBehavior.Restrict);
                c.HasOne(x => x.Organizador).WithMany().HasForeignKey(x => x.OrganizadorId).OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<Liquidacion>(l =>
            {
                l.HasIndex(x => new { x.OrganizadorId, x.EventoId });
                l.Property(x => x.Monto).HasPrecision(10, 2);
                l.HasOne(x => x.Organizador).WithMany().HasForeignKey(x => x.OrganizadorId).OnDelete(DeleteBehavior.Restrict);
                l.HasOne(x => x.Evento).WithMany().HasForeignKey(x => x.EventoId).OnDelete(DeleteBehavior.Restrict);
            });

            // 🧭 Historial para recomendaciones (HU17): se borra junto con el usuario
            builder.Entity<HistorialBusqueda>(h =>
            {
                h.Property(x => x.Termino).HasMaxLength(100);
                h.HasIndex(x => new { x.UsuarioId, x.Fecha });
                h.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<VisualizacionEvento>(v =>
            {
                v.HasIndex(x => new { x.UsuarioId, x.Fecha });
                v.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Cascade);
                v.HasOne<Evento>().WithMany().HasForeignKey(x => x.EventoId).OnDelete(DeleteBehavior.Cascade);
            });

            // HU22: auditoría de la analítica por usuario (se conserva aunque se borre el usuario consultado)
            builder.Entity<AuditoriaAnalitica>(a =>
            {
                a.HasIndex(x => x.Fecha);
                a.HasOne(x => x.Administrador).WithMany().HasForeignKey(x => x.AdministradorId).OnDelete(DeleteBehavior.Restrict);
            });

            // HU25: cada asiento tiene zona, fila y número únicos
            builder.Entity<Asiento>(a =>
            {
                a.HasIndex(x => new { x.TipoEntradaId, x.Fila, x.Numero }).IsUnique();
                a.HasOne(x => x.TipoEntrada).WithMany(t => t.Asientos).HasForeignKey(x => x.TipoEntradaId).OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<Evento>().Property(e => e.PlantillaPlano).HasConversion<string>().HasMaxLength(20);
            builder.Entity<ConsultaAsistente>().HasIndex(c => c.Fecha);
            builder.Entity<SolicitudOrganizador>(s =>
            {
                s.Property(x => x.Tipo).HasConversion<string>().HasMaxLength(20);
                s.Property(x => x.PlazoSolicitado).HasConversion<string>().HasMaxLength(20);
                s.Property(x => x.PlazoAprobado).HasConversion<string>().HasMaxLength(20);
                s.HasIndex(x => new { x.Estado, x.FechaSolicitud });
                s.HasOne(x => x.Usuario).WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Cascade);
            });
            builder.Entity<SolicitudPersonal>(s =>
            {
                s.HasIndex(x => new { x.Estado, x.FechaSolicitud });
                s.HasOne(x => x.Organizador).WithMany().HasForeignKey(x => x.OrganizadorId).OnDelete(DeleteBehavior.Cascade);
                s.HasOne(x => x.Evento).WithMany().HasForeignKey(x => x.EventoId).OnDelete(DeleteBehavior.Cascade);
            });

            // HU33: una calificación por entrada; la reputación se calcula por persona del personal
            builder.Entity<CalificacionPersonal>(c =>
            {
                c.HasIndex(x => x.EntradaId).IsUnique();
                c.HasIndex(x => x.PersonalId);
                c.HasOne(x => x.Entrada).WithMany().HasForeignKey(x => x.EntradaId).OnDelete(DeleteBehavior.Cascade);
                c.HasOne(x => x.Evento).WithMany().HasForeignKey(x => x.EventoId).OnDelete(DeleteBehavior.Restrict);
                c.HasOne(x => x.Personal).WithMany().HasForeignKey(x => x.PersonalId).OnDelete(DeleteBehavior.Cascade);
                c.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Restrict);
            });

            // HU34: cobro de publicación de eventos gratuitos y «Me interesa» (uno por usuario y evento)
            builder.Entity<PagoPublicacion>(p =>
            {
                p.HasIndex(x => x.Codigo).IsUnique();
                p.HasIndex(x => new { x.EventoId, x.Estado });
                p.HasOne(x => x.Evento).WithMany().HasForeignKey(x => x.EventoId).OnDelete(DeleteBehavior.Cascade);
                p.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OrganizadorId).OnDelete(DeleteBehavior.Restrict);
            });
            builder.Entity<InteresEvento>(i =>
            {
                i.HasIndex(x => new { x.EventoId, x.UsuarioId }).IsUnique();
                i.HasOne(x => x.Evento).WithMany().HasForeignKey(x => x.EventoId).OnDelete(DeleteBehavior.Cascade);
                i.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Cascade);
            });
            builder.Entity<RecordatorioEvento>(r =>
            {
                r.HasIndex(x => new { x.EventoId, x.UsuarioId }).IsUnique();
                r.HasIndex(x => x.EnviadoEn);
                r.HasOne(x => x.Evento).WithMany().HasForeignKey(x => x.EventoId).OnDelete(DeleteBehavior.Cascade);
                r.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Cascade);
            });
            builder.Entity<Notificacion>(n =>
            {
                n.HasIndex(x => new { x.UsuarioId, x.Leida, x.Fecha });
                n.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Cascade);
            });
            builder.Entity<AlertaAsistente>(a =>
            {
                a.HasIndex(x => new { x.Activa, x.Tipo });
                a.HasIndex(x => new { x.UsuarioId, x.Tipo, x.EventoId });
                a.HasOne(x => x.Evento).WithMany().HasForeignKey(x => x.EventoId).OnDelete(DeleteBehavior.Cascade);
                a.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Cascade);
            });

            // HU30: un cupón por cliente y promoción, código único y aplicado como máximo en una compra
            builder.Entity<Promocion>(p =>
            {
                p.Property(x => x.TipoDescuento).HasConversion<string>().HasMaxLength(20);
                p.HasOne(x => x.CreadaPor).WithMany().HasForeignKey(x => x.CreadaPorId).OnDelete(DeleteBehavior.Restrict);
            });
            builder.Entity<Cupon>(c =>
            {
                c.HasIndex(x => x.Codigo).IsUnique();
                c.HasIndex(x => new { x.PromocionId, x.UsuarioId }).IsUnique();
                c.HasIndex(x => x.OrdenId).IsUnique().HasFilter("OrdenId IS NOT NULL");
                c.HasOne(x => x.Promocion).WithMany(p => p.Cupones).HasForeignKey(x => x.PromocionId).OnDelete(DeleteBehavior.Cascade);
                c.HasOne(x => x.Usuario).WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Cascade);
                c.HasOne(x => x.Orden).WithMany().HasForeignKey(x => x.OrdenId).OnDelete(DeleteBehavior.SetNull);
            });
            builder.Entity<Orden>().HasOne(o => o.Cupon).WithMany().HasForeignKey(o => o.CuponId).OnDelete(DeleteBehavior.SetNull);

            // Un mismo miembro del personal se asigna una sola vez a cada evento
            builder.Entity<AsignacionAcceso>(a =>
            {
                a.HasIndex(x => new { x.UsuarioId, x.EventoId }).IsUnique();
                a.HasOne(x => x.Usuario).WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Cascade);
                a.HasOne(x => x.Evento).WithMany().HasForeignKey(x => x.EventoId).OnDelete(DeleteBehavior.Cascade);
            });

            // SQLite no ordena ni agrega columnas decimal: en ese proveedor todos los montos se guardan como REAL
            if (Database.IsSqlite())
            {
                foreach (var propiedad in builder.Model.GetEntityTypes()
                             .SelectMany(t => t.GetProperties())
                             .Where(p => p.ClrType == typeof(decimal)))
                {
                    propiedad.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<decimal, double>(
                        v => (double)v, v => (decimal)v));
                }
            }
        }

        // HU11: toda zona modificada con el seguimiento de EF sube su versión; si otra operación la cambió
        // desde que se leyó, SaveChanges lanza DbUpdateConcurrencyException en lugar de pisar los datos
        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            SubirVersiones();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            SubirVersiones();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        private void SubirVersiones()
        {
            foreach (var zona in ChangeTracker.Entries<TipoEntrada>().Where(e => e.State == EntityState.Modified))
                zona.Entity.RowVersion++;
        }
    }
}
