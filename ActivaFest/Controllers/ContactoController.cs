using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ActivaFest.Controllers
{
    // Datos de contacto del equipo administrador (configurables en appsettings: "Contacto")
    public class ContactoOptions
    {
        // Número con código de país, por ejemplo 51987654321 (vacío = no se muestra el botón de WhatsApp)
        public string WhatsApp { get; set; } = "";
        public string Correo { get; set; } = "soporte@activafest.com";

        public string WhatsAppSoloDigitos => Regex.Replace(WhatsApp ?? "", @"\D", "");
    }

    // =============================
    // 📞 CONTACTO CON EL ADMINISTRADOR (HU32)
    // =============================
    // Canales para pedir que habiliten una cuenta de organizador o el personal de control:
    // las solicitudes dentro de la página, WhatsApp y correo.
    public class ContactoController : Controller
    {
        private readonly ContactoOptions _contacto;

        public ContactoController(IOptions<ContactoOptions> contacto)
        {
            _contacto = contacto.Value;
        }

        public IActionResult Index() => View(_contacto);
    }
}
