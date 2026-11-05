using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ActivaFest.Models;
using ActivaFest.Services.Email;

namespace ActivaFest.Controllers
{
    // =============================
    // 🔐 AUTENTICACIÓN Y CUENTAS (HU01)
    // =============================
    public class AuthController : Controller
    {
        public const string MensajeLoginFallido =
            "No pudimos iniciar sesión con esos datos. Revisa tu correo y contraseña; tras 5 intentos fallidos el acceso se bloquea 10 minutos.";

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly IEmailService _email;
        private readonly ILogger<AuthController> _logger;

        public AuthController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            IEmailService email,
            ILogger<AuthController> logger)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _email = email;
            _logger = logger;
        }

        // =============================
        // 📝 REGISTRO
        // =============================
        [HttpGet]
        public IActionResult Registro()
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToAction("Index", "Home");

            return View(new RegistroViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Registro(RegistroViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var usuario = new ApplicationUser
            {
                UserName = model.Email,
                Email = model.Email,
                NombreCompleto = model.NombreCompleto.Trim(),
                AceptaTratamientoDatos = model.AceptaTratamientoDatos,
                AceptaPromociones = model.AceptaPromociones
            };

            var result = await _userManager.CreateAsync(usuario, model.Password);
            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                    ModelState.AddModelError(string.Empty, TraducirError(error));

                return View(model);
            }

            // HU31: toda cuenta nueva es Usuario; para organizar eventos se solicita al administrador
            await _userManager.AddToRoleAsync(usuario, Roles.Asistente);
            _logger.LogInformation("Nuevo usuario registrado: {Email}", usuario.Email);

            var urlLogin = Url.Action(nameof(Login), "Auth", null, Request.Scheme)!;
            await _email.EnviarAsync(usuario.Email!, "Bienvenido a ActivaFest",
                EmailTemplates.Bienvenida(usuario.NombreCompleto, Roles.NombreVisible(Roles.Asistente), urlLogin));

            await _signInManager.SignInAsync(usuario, isPersistent: false);
            TempData["Mensaje"] = $"¡Bienvenido/a, {usuario.NombreCompleto}! Tu cuenta fue creada.";

            return RedirectToAction("Index", "Home");
        }

        // =============================
        // 🔑 INICIO DE SESIÓN
        // =============================
        [HttpGet]
        public IActionResult Login(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToAction("Index", "Home");

            return View(new LoginViewModel { ReturnUrl = returnUrl });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var result = await _signInManager.PasswordSignInAsync(
                model.Email, model.Password, model.Recordarme, lockoutOnFailure: true);

            if (result.Succeeded)
            {
                if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
                    return Redirect(model.ReturnUrl);

                var usuario = await _userManager.FindByEmailAsync(model.Email);
                if (usuario != null && await _userManager.IsInRoleAsync(usuario, Roles.Administrador))
                    return RedirectToAction("Index", "Admin");

                return RedirectToAction("Index", "Home");
            }

            // Un único mensaje para todo fallo (correo inexistente, clave errada, cuenta bloqueada o desactivada):
            // no revela qué dato falló ni si el correo está registrado (criterio de HU01)
            ModelState.AddModelError(string.Empty, MensajeLoginFallido);

            return View(model);
        }

        // =============================
        // 🚪 CERRAR SESIÓN
        // =============================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
            HttpContext.Session.Clear();
            return RedirectToAction("Index", "Home");
        }

        // =============================
        // 🔁 RECUPERACIÓN DE CONTRASEÑA
        // =============================
        [HttpGet]
        public IActionResult OlvideContrasena() => View(new OlvideContrasenaViewModel());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OlvideContrasena(OlvideContrasenaViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var usuario = await _userManager.FindByEmailAsync(model.Email);

            // No revelamos si el correo existe o no; una cuenta desactivada tampoco recibe enlace (HU19)
            if (usuario is { Activo: true })
            {
                var token = await _userManager.GeneratePasswordResetTokenAsync(usuario);
                var url = Url.Action(nameof(RestablecerContrasena), "Auth",
                    new { email = usuario.Email, token }, Request.Scheme)!;

                await _email.EnviarAsync(usuario.Email!, "Restablecer contraseña - ActivaFest",
                    EmailTemplates.RecuperarContrasena(usuario.NombreCompleto, url));
            }

            TempData["Mensaje"] = "Si el correo está registrado, te enviamos un enlace para restablecer tu contraseña.";
            return RedirectToAction(nameof(Login));
        }

        [HttpGet]
        public IActionResult RestablecerContrasena(string? email, string? token)
        {
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(token))
                return RedirectToAction(nameof(Login));

            return View(new RestablecerContrasenaViewModel { Email = email, Token = token });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RestablecerContrasena(RestablecerContrasenaViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var usuario = await _userManager.FindByEmailAsync(model.Email);
            if (usuario != null)
            {
                var result = await _userManager.ResetPasswordAsync(usuario, model.Token, model.Password);
                if (!result.Succeeded)
                {
                    foreach (var error in result.Errors)
                        ModelState.AddModelError(string.Empty, TraducirError(error));

                    return View(model);
                }
            }

            TempData["Mensaje"] = "Tu contraseña fue actualizada. Ya puedes iniciar sesión.";
            return RedirectToAction(nameof(Login));
        }

        [HttpGet]
        public IActionResult AccesoDenegado() => View();

        private static string TraducirError(IdentityError error) => error.Code switch
        {
            "DuplicateUserName" or "DuplicateEmail" => "Ya existe una cuenta con ese correo.",
            "PasswordTooShort" => "La contraseña debe tener al menos 8 caracteres.",
            "PasswordRequiresUpper" => "La contraseña debe tener al menos una letra mayúscula.",
            "PasswordRequiresDigit" => "La contraseña debe tener al menos un número.",
            "PasswordRequiresLower" => "La contraseña debe tener al menos una letra minúscula.",
            "InvalidToken" => "El enlace de recuperación no es válido, ya fue usado o venció (dura 1 hora). Solicita uno nuevo.",
            _ => error.Description
        };
    }
}
