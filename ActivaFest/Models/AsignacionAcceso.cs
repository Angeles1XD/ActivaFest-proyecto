namespace ActivaFest.Models
{
    // ASIGNACION_ACCESO: personal de Control de Acceso habilitado para validar las entradas de un evento (HU19)
    public class AsignacionAcceso
    {
        public int Id { get; set; }

        public string UsuarioId { get; set; } = "";
        public ApplicationUser? Usuario { get; set; }

        public int EventoId { get; set; }
        public Evento? Evento { get; set; }

        public DateTime FechaAsignacion { get; set; } = DateTime.UtcNow;

        // HU33: cuando termina el plazo de ingreso del evento la asignación se desactiva sola (se conserva como historial)
        public DateTime? FinalizadaEn { get; set; }

        public bool Activa => FinalizadaEn == null;
    }
}
