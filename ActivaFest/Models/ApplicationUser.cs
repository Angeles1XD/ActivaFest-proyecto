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

        // Se pide al registrarse, solo para estadísticas agregadas por rango de edad.
        // Queda en null en las cuentas creadas antes de pedir este dato.
        public DateTime? FechaNacimiento { get; set; }
    }

    public static class EdadUsuario
    {
        // Ley N.° 29733: desde los 14 años una persona puede consentir por sí misma el tratamiento de sus datos
        public const int Minima = 14;
        public const int Maxima = 120;

        // Edad cumplida en una fecha (por ejemplo, el día del evento)
        public static int En(DateTime nacimiento, DateTime fecha)
        {
            var edad = fecha.Year - nacimiento.Year;
            return nacimiento.Date > fecha.Date.AddYears(-edad) ? edad - 1 : edad;
        }
    }
}
