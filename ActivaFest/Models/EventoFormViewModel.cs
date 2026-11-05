using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    public class TipoEntradaInput
    {
        // Null para zonas nuevas; con valor al editar una zona existente
        public int? Id { get; set; }

        // Solo informativo en la edición (lo vendido + reservado limita la capacidad mínima)
        public int Comprometidas { get; set; }

        [Required(ErrorMessage = "Indica el nombre de la zona.")]
        [StringLength(60)]
        public string Nombre { get; set; } = "General";

        [Range(0, 100000, ErrorMessage = "El precio debe estar entre 0 y 100 000.")]
        public decimal Precio { get; set; }

        [Range(1, 1000000, ErrorMessage = "La capacidad debe ser al menos 1.")]
        public int Capacidad { get; set; } = 100;

        // 🗺️ Plano del recinto (HU25)
        [StringLength(7)]
        public string? Color { get; set; }

        [StringLength(40)]
        public string? Puerta { get; set; }

        // Solo en teatro: la capacidad es Filas x AsientosPorFila
        public int? Filas { get; set; }
        public int? AsientosPorFila { get; set; }
    }

    // =============================
    // 📝 FORMULARIO DE PUBLICACIÓN DE EVENTO
    // =============================
    public class EventoFormViewModel : IValidatableObject
    {
        public const int MinFotos = 3;
        public const int MaxFotos = 10;
        public const int MaxTiposEntrada = 10;

        [Required(ErrorMessage = "El título es obligatorio.")]
        [StringLength(150)]
        [Display(Name = "Título")]
        public string Titulo { get; set; } = "";

        [Required(ErrorMessage = "Describe el evento.")]
        [StringLength(4000, MinimumLength = 20, ErrorMessage = "La descripción debe tener entre 20 y 4000 caracteres.")]
        [Display(Name = "Descripción")]
        public string Descripcion { get; set; } = "";

        [Required(ErrorMessage = "Selecciona una categoría.")]
        [Display(Name = "Categoría")]
        public int? CategoriaId { get; set; }

        [Required(ErrorMessage = "Indica la fecha y hora.")]
        [Display(Name = "Fecha y hora")]
        public DateTime? Fecha { get; set; }

        [Required(ErrorMessage = "La dirección es obligatoria.")]
        [StringLength(200)]
        [Display(Name = "Dirección del recinto")]
        public string Ubicacion { get; set; } = "";

        [Required(ErrorMessage = "Marca la ubicación del recinto en el mapa.")]
        [Range(-90, 90)]
        public double? Latitud { get; set; }

        [Required(ErrorMessage = "Marca la ubicación del recinto en el mapa.")]
        [Range(-180, 180)]
        public double? Longitud { get; set; }

        [Display(Name = "Plantilla del plano")]
        public PlantillaPlano Plantilla { get; set; } = PlantillaPlano.CampoAbierto;

        public List<TipoEntradaInput> TiposEntrada { get; set; } = new() { new TipoEntradaInput() };

        [Display(Name = "Fotos del recinto")]
        // Sin "required" implícito: al editar o al reusar fotos ya subidas no hace falta elegir archivos;
        // la cantidad mínima la comprueba Validate()
#nullable disable
        public List<IFormFile> Fotos { get; set; } = new();
#nullable restore

        // Plano oficial del recinto (imagen opcional que sube el organizador)
        [Display(Name = "Plano oficial del recinto")]
        public IFormFile? PlanoOficialArchivo { get; set; }

        public string? PlanoOficialActual { get; set; }

        public bool QuitarPlanoOficial { get; set; }

        // =============================
        // ✏️ EDICIÓN (HU09)
        // =============================
        public int? Id { get; set; }

        public bool EsEdicion => Id.HasValue;

        // Fotos ya guardadas (se muestran en el formulario) y las que el organizador marcó para quitar
        public List<EventoImagen> ImagenesActuales { get; set; } = new();

        public List<int> ImagenesEliminar { get; set; } = new();

        public List<int> ImagenesActualesIds { get; set; } = new();

        // Fotos y plano que ya se subieron en un envío anterior que tuvo errores: no hay que volver a elegirlos
        // (el navegador vacía el campo de archivos). Solo se aceptan las rutas guardadas en la sesión del usuario.
        public List<string> FotosSubidas { get; set; } = new();

        public string? PlanoSubido { get; set; }

        // HU25: cada plantilla tiene un máximo de zonas legible y en teatro la capacidad sale de filas x asientos
        private IEnumerable<ValidationResult> ValidarPlano()
        {
            if (!Enum.IsDefined(Plantilla))
            {
                yield return new ValidationResult("Elige una plantilla válida.", new[] { nameof(Plantilla) });
                yield break;
            }

            var maxZonas = Services.Plano.PlanoService.MaxZonas[Plantilla];
            if (TiposEntrada.Count > maxZonas)
                yield return new ValidationResult($"La plantilla {Services.Plano.PlanoService.Nombre(Plantilla)} admite como máximo {maxZonas} zonas.", new[] { nameof(TiposEntrada) });

            if (Plantilla != PlantillaPlano.Teatro)
                yield break;

            foreach (var zona in TiposEntrada)
            {
                if (zona.Filas is not (>= 1 and <= Services.Plano.PlanoService.MaxFilasTeatro) ||
                    zona.AsientosPorFila is not (>= 2 and <= Services.Plano.PlanoService.MaxAsientosPorFila))
                {
                    yield return new ValidationResult(
                        $"En la zona \"{zona.Nombre}\" indica entre 1 y {Services.Plano.PlanoService.MaxFilasTeatro} filas y entre 2 y {Services.Plano.PlanoService.MaxAsientosPorFila} asientos por fila.",
                        new[] { nameof(TiposEntrada) });
                }
                else if (zona.Capacidad != zona.Filas * zona.AsientosPorFila)
                {
                    yield return new ValidationResult($"La capacidad de la zona \"{zona.Nombre}\" debe ser filas × asientos por fila ({zona.Filas * zona.AsientosPorFila}).", new[] { nameof(TiposEntrada) });
                }
            }

            if (TiposEntrada.Sum(z => z.Filas ?? 0) > Services.Plano.PlanoService.MaxFilasTeatro)
                yield return new ValidationResult($"El teatro admite como máximo {Services.Plano.PlanoService.MaxFilasTeatro} filas en total.", new[] { nameof(TiposEntrada) });
        }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Fecha.HasValue && Fecha.Value <= DateTime.Now)
                yield return new ValidationResult("La fecha del evento debe ser futura.", new[] { nameof(Fecha) });

            if (TiposEntrada.Count == 0)
                yield return new ValidationResult("Agrega al menos un tipo de entrada.", new[] { nameof(TiposEntrada) });

            if (TiposEntrada.Count > MaxTiposEntrada)
                yield return new ValidationResult($"Máximo {MaxTiposEntrada} tipos de entrada.", new[] { nameof(TiposEntrada) });

            foreach (var error in ValidarPlano())
                yield return error;

            var nombres = TiposEntrada.Select(t => t.Nombre?.Trim().ToLowerInvariant()).ToList();
            if (nombres.Distinct().Count() != nombres.Count)
                yield return new ValidationResult("Los tipos de entrada no pueden repetir nombre.", new[] { nameof(TiposEntrada) });

            // En la edición cuentan también las fotos que se conservan (el servicio lo vuelve a comprobar contra la BD)
            var conservadas = ImagenesActualesIds.Except(ImagenesEliminar).Count();
            var fotos = Fotos.Where(f => f.Length > 0).Count() + conservadas + FotosSubidas.Count;
            if (fotos < MinFotos)
                yield return new ValidationResult($"Sube al menos {MinFotos} fotos del recinto.", new[] { nameof(Fotos) });

            if (fotos > MaxFotos)
                yield return new ValidationResult($"Puedes subir como máximo {MaxFotos} fotos.", new[] { nameof(Fotos) });
        }
    }
}
