using System.ComponentModel.DataAnnotations;

namespace ActivaFest.Models
{
    // AUDITORIA_ANALITICA (HU22 / RNF07, Ley N.° 29733): cada consulta de datos personales del administrador queda registrada
    public class AuditoriaAnalitica
    {
        public int Id { get; set; }

        public string AdministradorId { get; set; } = "";
        public ApplicationUser? Administrador { get; set; }

        [StringLength(200)]
        public string Consulta { get; set; } = "";

        [StringLength(200)]
        public string FuenteDatos { get; set; } = "";

        public DateTime Fecha { get; set; } = DateTime.UtcNow;
    }
}
