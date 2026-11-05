using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    public enum EstadoSolicitud
    {
        Pendiente = 0,
        Aprobada = 1,
        Rechazada = 2
    }

    public enum TipoOrganizador
    {
        [Display(Name = "Persona natural (DNI)")]
        PersonaNatural = 0,

        [Display(Name = "Empresa o asociación (RUC)")]
        Empresa = 1
    }

    // Por cuánto tiempo se habilita la cuenta para crear eventos (0 = indefinido)
    public enum PlazoOrganizador
    {
        [Display(Name = "30 días")]
        Dias30 = 30,

        [Display(Name = "90 días")]
        Dias90 = 90,

        [Display(Name = "180 días")]
        Dias180 = 180,

        [Display(Name = "Indefinido")]
        Indefinido = 0
    }

    // =============================
    // 📝 SOLICITUD PARA ORGANIZAR EVENTOS (HU31)
    // =============================
    // Todas las cuentas se crean como Usuario. Para publicar eventos, el usuario pide que lo habiliten como
    // organizador cumpliendo los requisitos; el administrador revisa, aprueba (por un tiempo o indefinido) o rechaza.
    // Así se evita que cualquiera cree eventos falsos.
    public class SolicitudOrganizador
    {
        public int Id { get; set; }

        [Required]
        public string UsuarioId { get; set; } = "";
        public ApplicationUser? Usuario { get; set; }

        public TipoOrganizador Tipo { get; set; }

        // Nombre completo o razón social
        [Required, StringLength(150)]
        public string NombreOrganizador { get; set; } = "";

        // DNI (8 dígitos) o RUC (11 dígitos)
        [Required, StringLength(11)]
        public string Documento { get; set; } = "";

        [Required, StringLength(20)]
        public string Telefono { get; set; } = "";

        // Qué eventos organizará (tipo, lugar aproximado y público)
        [Required, StringLength(1000)]
        public string DescripcionEventos { get; set; } = "";

        [StringLength(300)]
        public string? WebORedes { get; set; }

        public PlazoOrganizador PlazoSolicitado { get; set; }

        public EstadoSolicitud Estado { get; set; } = EstadoSolicitud.Pendiente;

        public DateTime FechaSolicitud { get; set; } = DateTime.UtcNow;

        // Respuesta del administrador
        public DateTime? FechaRespuesta { get; set; }
        public string? RespondidaPorId { get; set; }
        public PlazoOrganizador? PlazoAprobado { get; set; }
        public DateTime? HabilitadoHasta { get; set; }

        [StringLength(500)]
        public string? MotivoRechazo { get; set; }
    }
}
