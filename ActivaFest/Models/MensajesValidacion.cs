using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace ActivaFest.Models;

// =============================
// 🇵🇪 MENSAJES DE VALIDACIÓN EN ESPAÑOL
// =============================
// Los atributos sin ErrorMessage propio y los errores del model binding salían en inglés
// ("The X field is required.", "The value '' is invalid.").
public class MensajesValidacionEnEspanol : IValidationMetadataProvider
{
    public void CreateValidationMetadata(ValidationMetadataProviderContext context)
    {
        foreach (var atributo in context.ValidationMetadata.ValidatorMetadata.OfType<ValidationAttribute>())
        {
            if (!string.IsNullOrEmpty(atributo.ErrorMessage) || atributo.ErrorMessageResourceType != null)
                continue;

            atributo.ErrorMessage = atributo switch
            {
                RequiredAttribute => "El campo {0} es obligatorio.",
                StringLengthAttribute { MinimumLength: > 0 } => "{0} debe tener entre {2} y {1} caracteres.",
                StringLengthAttribute => "{0} puede tener como máximo {1} caracteres.",
                MaxLengthAttribute => "{0} puede tener como máximo {1} caracteres.",
                MinLengthAttribute => "{0} debe tener al menos {1} caracteres.",
                RangeAttribute => "{0} debe estar entre {1} y {2}.",
                EmailAddressAttribute => "{0} no es un correo válido.",
                PhoneAttribute => "{0} no es un teléfono válido.",
                UrlAttribute => "{0} no es una dirección web válida.",
                CompareAttribute => "{0} y {1} no coinciden.",
                RegularExpressionAttribute => "{0} no tiene un formato válido.",
                _ => null
            };
        }
    }

    public static void Configurar(MvcOptions options)
    {
        options.ModelMetadataDetailsProviders.Add(new MensajesValidacionEnEspanol());

        var m = options.ModelBindingMessageProvider;
        m.SetValueMustNotBeNullAccessor(_ => "Este campo es obligatorio.");
        m.SetMissingBindRequiredValueAccessor(campo => $"Falta el valor de {campo}.");
        m.SetMissingKeyOrValueAccessor(() => "Falta un valor obligatorio.");
        m.SetMissingRequestBodyRequiredValueAccessor(() => "Faltan los datos del formulario.");
        m.SetAttemptedValueIsInvalidAccessor((valor, campo) => $"El valor «{valor}» no es válido para {campo}.");
        m.SetNonPropertyAttemptedValueIsInvalidAccessor(valor => $"El valor «{valor}» no es válido.");
        m.SetUnknownValueIsInvalidAccessor(campo => $"El valor de {campo} no es válido.");
        m.SetNonPropertyUnknownValueIsInvalidAccessor(() => "El valor no es válido.");
        m.SetValueIsInvalidAccessor(valor => $"El valor «{valor}» no es válido.");
        m.SetValueMustBeANumberAccessor(campo => $"{campo} debe ser un número.");
        m.SetNonPropertyValueMustBeANumberAccessor(() => "Debe ser un número.");
    }
}
