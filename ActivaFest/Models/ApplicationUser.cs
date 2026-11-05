using Microsoft.AspNetCore.Identity;

namespace ActivaFest.Models
{
    // Entidad USUARIO del modelo de datos (Identity aporta Id, Email y PasswordHash)
    public class ApplicationUser : IdentityUser
    {
        public string NombreCompleto { get; set; } = "";

        public DateTime FechaRegistro { get; set; } = DateTime.UtcNow;

        // Una cuenta desactivada por el administrador no puede iniciar sesión (HU19)
        public bool Activo { get; set; } = true;

        // Ley N.° 29733 (RNF07): consentimiento explícito al registrarse y,
        // por separado y opcional, para recibir promociones
        public bool AceptaTratamientoDatos { get; set; }

        public bool AceptaPromociones { get; set; }

        // HU31: hasta cuándo puede crear eventos como organizador (null = indefinido). Al vencer, pierde el rol.
        public DateTime? OrganizadorHasta { get; set; }
    }
}
