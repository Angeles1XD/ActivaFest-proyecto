using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using ActivaFest.Models;

namespace ActivaFest.Services;

// Una cuenta desactivada por el administrador no puede iniciar sesión: el inicio devuelve
// "NotAllowed" y la pantalla muestra el mismo mensaje genérico que con una clave errada.
public class SignInManagerActivaFest : SignInManager<ApplicationUser>
{
    public SignInManagerActivaFest(
        UserManager<ApplicationUser> userManager,
        IHttpContextAccessor contextAccessor,
        IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
        IOptions<IdentityOptions> optionsAccessor,
        ILogger<SignInManager<ApplicationUser>> logger,
        IAuthenticationSchemeProvider schemes,
        IUserConfirmation<ApplicationUser> confirmation)
        : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
    {
    }

    public override async Task<bool> CanSignInAsync(ApplicationUser user) =>
        user.Activo && await base.CanSignInAsync(user);
}
