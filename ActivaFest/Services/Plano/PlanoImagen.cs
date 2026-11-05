using System.Buffers.Binary;
using System.IO.Compression;
using ActivaFest.Models;

namespace ActivaFest.Services.Plano;

// =============================
// 🖼️ PLANO EN PNG PARA EL CORREO (HU27)
// =============================
// Los clientes de correo no muestran SVG, así que el plano con la ubicación resaltada se envía como imagen PNG.
// Se dibuja con la misma geometría del SVG (PlanoService) y se codifica a PNG sin librerías nativas, así funciona
// igual en Windows y en el contenedor Linux de Render. Lo resaltado va en rosa; el resto, atenuado.
public static class PlanoImagen
{
    private const double Escala = 0.75;
    private const int AnchoPlano = 800;

    private static readonly (byte R, byte G, byte B) Fondo = (18, 18, 26);
    private static readonly (byte R, byte G, byte B) Sala = (26, 26, 36);
    private static readonly (byte R, byte G, byte B) Telon = (127, 29, 58);
    private static readonly (byte R, byte G, byte B) Tablado = (82, 63, 49);
    private static readonly (byte R, byte G, byte B) Cesped = (21, 128, 61);
    private static readonly (byte R, byte G, byte B) CespedOscuro = (22, 101, 52);
    private static readonly (byte R, byte G, byte B) Terreno = (15, 35, 24);
    private static readonly (byte R, byte G, byte B) Tribuna = (40, 40, 52);
    private static readonly (byte R, byte G, byte B) Resaltado = (236, 72, 153);
    private static readonly (byte R, byte G, byte B) Blanco = (255, 255, 255);

    public static byte[] GenerarPng(PlantillaPlano plantilla, IReadOnlyList<ZonaPlano> zonas, IReadOnlyList<AsientoPlano> asientos,
        IReadOnlySet<int> asientosResaltados, IReadOnlySet<int> zonasResaltadas)
    {
        var alto = PlanoService.AltoPlano(plantilla, zonas.Count, asientos);
        var lienzo = new Lienzo((int)(AnchoPlano * Escala), (int)(alto * Escala));
        lienzo.Rect(0, 0, AnchoPlano, alto, 0, Fondo);

        var colores = zonas.ToDictionary(z => z.Id, z => Rgb(z.Color));

        switch (plantilla)
        {
            case PlantillaPlano.Teatro:
                lienzo.Rect(18, 8, 764, alto - 16, 28, Sala);
                lienzo.Rect(186, 14, 428, 24, 4, Telon);
                lienzo.Rect(186, 38, 428, 60, 18, Tablado);
                // Butacas: las del comprador en rosa con borde blanco, el resto atenuadas
                foreach (var a in asientos.Where(a => !asientosResaltados.Contains(a.Id)))
                    lienzo.Rect(a.X - 8.5, a.Y - 8, 17, 15, 4, Atenuar(colores.GetValueOrDefault(a.ZonaId, Tablado)));
                foreach (var a in asientos.Where(a => asientosResaltados.Contains(a.Id)))
                {
                    lienzo.Rect(a.X - 12.5, a.Y - 12, 25, 23, 6, Blanco);
                    lienzo.Rect(a.X - 10.5, a.Y - 10, 21, 19, 5, Resaltado);
                }
                break;

            case PlantillaPlano.Estadio:
                lienzo.Rect(18, 10, 764, 494, 140, Sala);
                for (var i = zonas.Count; i < 4; i++)
                    lienzo.Poligono(PlanoService.PoligonoZona(plantilla, i, zonas.Count), Tribuna);
                for (var k = 0; k < 10; k++)
                    lienzo.Rect(250 + k * 30, 170, 30, 180, 0, k % 2 == 0 ? Cesped : CespedOscuro);
                ZonasDe(lienzo, plantilla, zonas, colores, zonasResaltadas);
                break;

            case PlantillaPlano.Feria:
                lienzo.Rect(30, 18, 740, 318, 16, Sala);
                for (var f = 0; f < 3; f++)
                    for (var c = 0; c < 5; c++)
                        lienzo.Rect(56 + c * 100, 36 + f * 96, 84, 66, 5, (45, 45, 58));
                lienzo.Rect(566, 40, 184, 262, 10, (34, 34, 44));
                ZonasDe(lienzo, plantilla, zonas, colores, zonasResaltadas);
                break;

            case PlantillaPlano.Coliseo:
                ZonasDe(lienzo, plantilla, zonas, colores, zonasResaltadas);
                var cy = PlanoService.CentroColiseoY(Math.Min(zonas.Count, PlanoService.MaxZonas[plantilla]));
                lienzo.Rect(346, cy - 40, 108, 80, 4, (229, 231, 235));
                break;

            case PlantillaPlano.Discoteca:
                lienzo.Rect(20, 10, 760, 440, 20, Sala);
                ZonasDe(lienzo, plantilla, zonas, colores, zonasResaltadas);
                lienzo.Rect(330, 24, 140, 46, 8, (39, 39, 42));
                break;

            default:
                lienzo.Rect(12, 8, 776, alto - 16, 24, Terreno);
                lienzo.Poligono(new[] { (246.0, 22.0), (554.0, 22.0), (588.0, 92.0), (212.0, 92.0) }, Tablado);
                ZonasDe(lienzo, plantilla, zonas, colores, zonasResaltadas);
                break;
        }

        return lienzo.Png();
    }

    private static void ZonasDe(Lienzo lienzo, PlantillaPlano plantilla, IReadOnlyList<ZonaPlano> zonas,
        Dictionary<int, (byte, byte, byte)> colores, IReadOnlySet<int> resaltadas)
    {
        var total = Math.Min(zonas.Count, PlanoService.MaxZonas[plantilla]);
        for (var i = 0; i < total; i++)
        {
            var contorno = PlanoService.PoligonoZona(plantilla, i, total);
            lienzo.Poligono(contorno, resaltadas.Contains(zonas[i].Id) ? Resaltado : Atenuar(colores[zonas[i].Id]));
        }
    }

    private static (byte R, byte G, byte B) Rgb(string hex) =>
        (Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));

    // Mezcla con el fondo para que lo resaltado destaque
    private static (byte R, byte G, byte B) Atenuar((byte R, byte G, byte B) c) =>
        ((byte)((c.R + Fondo.R * 2) / 3), (byte)((c.G + Fondo.G * 2) / 3), (byte)((c.B + Fondo.B * 2) / 3));

    // Lienzo RGB mínimo: rectángulos redondeados, círculos y polígonos, en coordenadas del plano (escaladas)
    private sealed class Lienzo
    {
        private readonly int _ancho, _alto;
        private readonly byte[] _pixeles;

        public Lienzo(int ancho, int alto)
        {
            _ancho = ancho;
            _alto = alto;
            _pixeles = new byte[ancho * alto * 3];
        }

        private void Pintar(int px, int py, (byte R, byte G, byte B) c)
        {
            if (px < 0 || py < 0 || px >= _ancho || py >= _alto) return;
            var i = (py * _ancho + px) * 3;
            _pixeles[i] = c.R;
            _pixeles[i + 1] = c.G;
            _pixeles[i + 2] = c.B;
        }

        public void Circulo(double cx, double cy, double r, (byte, byte, byte) color)
        {
            double x0 = cx * Escala, y0 = cy * Escala, rr = r * Escala;
            for (var py = (int)(y0 - rr); py <= (int)(y0 + rr); py++)
                for (var px = (int)(x0 - rr); px <= (int)(x0 + rr); px++)
                    if ((px + 0.5 - x0) * (px + 0.5 - x0) + (py + 0.5 - y0) * (py + 0.5 - y0) <= rr * rr)
                        Pintar(px, py, color);
        }

        public void Rect(double x, double y, double w, double h, double radio, (byte, byte, byte) color)
        {
            double x0 = x * Escala, y0 = y * Escala, x1 = (x + w) * Escala, y1 = (y + h) * Escala, r = radio * Escala;
            for (var py = (int)y0; py < (int)Math.Ceiling(y1); py++)
                for (var px = (int)x0; px < (int)Math.Ceiling(x1); px++)
                {
                    // Esquinas redondeadas: fuera del radio en las esquinas no se pinta
                    var dx = Math.Max(Math.Max(x0 + r - (px + 0.5), px + 0.5 - (x1 - r)), 0);
                    var dy = Math.Max(Math.Max(y0 + r - (py + 0.5), py + 0.5 - (y1 - r)), 0);
                    if (dx * dx + dy * dy <= r * r)
                        Pintar(px, py, color);
                }
        }

        // Relleno por barrido (regla par-impar): sirve para tribunas, abanicos y cualquier contorno cerrado
        public void Poligono(IReadOnlyList<(double X, double Y)> puntos, (byte, byte, byte) color)
        {
            var p = puntos.Select(q => (X: q.X * Escala, Y: q.Y * Escala)).ToList();
            var minY = Math.Max(0, (int)p.Min(q => q.Y));
            var maxY = Math.Min(_alto - 1, (int)Math.Ceiling(p.Max(q => q.Y)));
            var cortes = new List<double>();
            for (var py = minY; py <= maxY; py++)
            {
                var y = py + 0.5;
                cortes.Clear();
                for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
                    if ((p[i].Y <= y) != (p[j].Y <= y))
                        cortes.Add(p[i].X + (y - p[i].Y) / (p[j].Y - p[i].Y) * (p[j].X - p[i].X));
                cortes.Sort();
                for (var k = 0; k + 1 < cortes.Count; k += 2)
                    for (var px = (int)Math.Ceiling(cortes[k] - 0.5); px + 0.5 <= cortes[k + 1]; px++)
                        Pintar(px, py, color);
            }
        }

        // Codificación PNG estándar: firma + IHDR + IDAT (zlib) + IEND
        public byte[] Png()
        {
            using var crudo = new MemoryStream();
            using (var zlib = new ZLibStream(crudo, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (var y = 0; y < _alto; y++)
                {
                    zlib.WriteByte(0); // sin filtro
                    zlib.Write(_pixeles, y * _ancho * 3, _ancho * 3);
                }
            }

            using var png = new MemoryStream();
            png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            var cabecera = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(cabecera, _ancho);
            BinaryPrimitives.WriteInt32BigEndian(cabecera.AsSpan(4), _alto);
            cabecera[8] = 8;  // 8 bits por canal
            cabecera[9] = 2;  // RGB
            Bloque(png, "IHDR", cabecera);
            Bloque(png, "IDAT", crudo.ToArray());
            Bloque(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        private static void Bloque(Stream salida, string tipo, byte[] datos)
        {
            Span<byte> numero = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(numero, datos.Length);
            salida.Write(numero);

            var tipoYDatos = new byte[4 + datos.Length];
            System.Text.Encoding.ASCII.GetBytes(tipo, tipoYDatos);
            datos.CopyTo(tipoYDatos, 4);
            salida.Write(tipoYDatos);

            BinaryPrimitives.WriteUInt32BigEndian(numero, Crc32(tipoYDatos));
            salida.Write(numero);
        }

        private static readonly uint[] TablaCrc = Enumerable.Range(0, 256).Select(n =>
        {
            var c = (uint)n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        private static uint Crc32(byte[] datos)
        {
            var c = 0xFFFFFFFF;
            foreach (var b in datos)
                c = TablaCrc[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFF;
        }
    }
}
