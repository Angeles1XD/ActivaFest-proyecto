using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    // Casilla obligatoria (consentimiento): se valida en el servidor; en el navegador la exige el atributo "required"
    public class DebeEstarMarcadoAttribute : ValidationAttribute
    {
        public override bool IsValid(object? value) => value is true;
    }

    public class LoginViewModel
    {
        [Required(ErrorMessage = "Ingresa tu correo.")]
        [EmailAddress(ErrorMessage = "Correo no válido.")]
        [Display(Name = "Correo electrónico")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Ingresa tu contraseña.")]
        [DataType(DataType.Password)]
        [Display(Name = "Contraseña")]
        public string Password { get; set; } = "";

        [Display(Name = "Recordarme")]
        public bool Recordarme { get; set; }

        public string? ReturnUrl { get; set; }
    }

    public class RegistroViewModel
    {
        [Required(ErrorMessage = "Ingresa tu nombre.")]
        [StringLength(100)]
        [Display(Name = "Nombre completo")]
        public string NombreCompleto { get; set; } = "";

        [Required(ErrorMessage = "Ingresa tu correo.")]
        [EmailAddress(ErrorMessage = "Correo no válido.")]
        [Display(Name = "Correo electrónico")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Ingresa una contraseña.")]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "La contraseña debe tener al menos 8 caracteres.")]
        [DataType(DataType.Password)]
        [Display(Name = "Contraseña")]
        public string Password { get; set; } = "";

        [Required(ErrorMessage = "Confirma tu contraseña.")]
        [DataType(DataType.Password)]
        [Compare(nameof(Password), ErrorMessage = "Las contraseñas no coinciden.")]
        [Display(Name = "Confirmar contraseña")]
        public string ConfirmarPassword { get; set; } = "";

        // HU31: ya no se elige el tipo de cuenta. Todas nacen como Usuario; para crear eventos se solicita al administrador.

        // Obligatorio (RNF07): sin consentimiento no se crea la cuenta
        // No usar [Range(true, true)]: su validación en el navegador (jQuery) rechaza la casilla aunque esté marcada
        [DebeEstarMarcado(ErrorMessage = "Debes aceptar la política de privacidad para crear tu cuenta.")]
        public bool AceptaTratamientoDatos { get; set; }

        // Opcional y separado del anterior
        public bool AceptaPromociones { get; set; }
    }

    public class OlvideContrasenaViewModel
    {
        [Required(ErrorMessage = "Ingresa tu correo.")]
        [EmailAddress(ErrorMessage = "Correo no válido.")]
        [Display(Name = "Correo electrónico")]
        public string Email { get; set; } = "";
    }

    public class RestablecerContrasenaViewModel
    {
        [Required]
        public string Email { get; set; } = "";

        [Required]
        public string Token { get; set; } = "";

        [Required(ErrorMessage = "Ingresa una contraseña.")]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "La contraseña debe tener al menos 8 caracteres.")]
        [DataType(DataType.Password)]
        [Display(Name = "Nueva contraseña")]
        public string Password { get; set; } = "";

        [Required(ErrorMessage = "Confirma tu contraseña.")]
        [DataType(DataType.Password)]
        [Compare(nameof(Password), ErrorMessage = "Las contraseñas no coinciden.")]
        [Display(Name = "Confirmar contraseña")]
        public string ConfirmarPassword { get; set; } = "";
    }
}
