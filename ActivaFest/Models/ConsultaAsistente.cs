using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    // =============================
    // 📊 CONSULTA AL ASISTENTE (HU14 / métrica del OE3)
    // =============================
    // Cada pregunta al chat se registra para medir cuántas se resuelven sin ayuda humana (meta: 80 %) y
    // para mejorar la base de conocimiento con las no resueltas. No guarda quién preguntó.
    public class ConsultaAsistente
    {
        public int Id { get; set; }

        public DateTime Fecha { get; set; } = DateTime.UtcNow;

        [StringLength(200)]
        public string Pregunta { get; set; } = "";

        [StringLength(40)]
        public string Intencion { get; set; } = "";

        public bool Resuelta { get; set; }

        public int? EventoId { get; set; }

        // Si quien preguntó tenía sesión iniciada (sin guardar su identidad)
        public bool ConSesion { get; set; }

        // Si Llama 3.2 (Ollama) redactó la respuesta
        public bool RedactadaPorLlm { get; set; }
    }
}
