using Microsoft.ML;
using ActivaFest.Models.ML;
using ActivaFest.Services.Asistente;

namespace ActivaFest.Services;

public record PrediccionIntencion(string Intencion, float Confianza);

// =============================
// 🧠 CLASIFICADOR DE INTENCIONES CON ML.NET (HU14)
// =============================
// Se entrena al arrancar con los ejemplos y palabras clave de la base de conocimiento.
// El PredictionEngine no es seguro para varios hilos: se usa con un lock (el modelo es pequeño y responde en microsegundos).
public class MachineLearningService
{
    private readonly MLContext _mlContext = new(seed: 0);
    private readonly PredictionEngine<UserInput, IntentPrediction> _motor;
    private readonly object _candado = new();

    public MachineLearningService()
    {
        var datos = BaseConocimiento.Entradas
            .SelectMany(e => e.Ejemplos.Concat(e.PalabrasClave).Select(t => new UserInput
            {
                Text = BaseConocimiento.Normalizar(t),
                Intent = e.Intencion
            }))
            .ToList();

        var dataView = _mlContext.Data.LoadFromEnumerable(datos);

        var pipeline = _mlContext.Transforms.Conversion.MapValueToKey("Label", nameof(UserInput.Intent))
            .Append(_mlContext.Transforms.Text.FeaturizeText("Features", nameof(UserInput.Text)))
            .Append(_mlContext.MulticlassClassification.Trainers.SdcaMaximumEntropy("Label", "Features"))
            .Append(_mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

        var modelo = pipeline.Fit(dataView);
        _motor = _mlContext.Model.CreatePredictionEngine<UserInput, IntentPrediction>(modelo);

    }

    public PrediccionIntencion Predecir(string mensaje)
    {
        IntentPrediction prediccion;
        lock (_candado)
            prediccion = _motor.Predict(new UserInput { Text = BaseConocimiento.Normalizar(mensaje) });

        var confianza = prediccion.Score.Length == 0 ? 0 : prediccion.Score.Max();
        return new PrediccionIntencion(prediccion.PredictedIntent, confianza);
    }

    // Compatibilidad con el código anterior
    public string ClasificarIntencion(string mensaje) => Predecir(mensaje).Intencion;

}
