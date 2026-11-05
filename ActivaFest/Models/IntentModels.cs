using Microsoft.ML.Data;

namespace ActivaFest.Models.ML;

public class UserInput
{
    [LoadColumn(0)] public string Text { get; set; } = string.Empty;

    [LoadColumn(1)] public string Intent { get; set; } = string.Empty;
}

public class IntentPrediction
{
    [ColumnName("PredictedLabel")] public string PredictedIntent { get; set; } = string.Empty;

    public float[] Score { get; set; } = [];
}