// =============================
// 📈 GRÁFICO DEL COEFICIENTE DE VARIACIÓN POR EVENTO (HU23)
// =============================
// Barras con el color del semáforo y dos líneas guía con los umbrales configurados.
(function () {
    'use strict';

    const fuente = document.getElementById('datosVariabilidad');
    const lienzo = document.getElementById('graficoCv');
    if (!fuente || !lienzo || typeof Chart === 'undefined') return;

    const datos = JSON.parse(fuente.textContent);
    if (!datos.eventos.length) return;

    const etiquetas = datos.eventos.map(function (e) { return e.titulo; });
    const linea = function (valor, texto, color) {
        return {
            type: 'line', label: texto, data: etiquetas.map(function () { return valor; }),
            borderColor: color, borderDash: [6, 4], borderWidth: 1.5, pointRadius: 0, fill: false
        };
    };

    new Chart(lienzo, {
        data: {
            labels: etiquetas,
            datasets: [
                { type: 'bar', label: 'CV (%)', data: datos.eventos.map(function (e) { return e.cv; }), backgroundColor: datos.eventos.map(function (e) { return e.color; }), borderRadius: 6 },
                linea(datos.estable, 'Umbral estable', '#22c55e'),
                linea(datos.inestable, 'Umbral inestable', '#ef4444')
            ]
        },
        options: {
            responsive: true, maintainAspectRatio: false,
            plugins: { legend: { labels: { color: '#c4c4d4' } } },
            scales: {
                x: { ticks: { color: '#a1a1b5', maxRotation: 35 } },
                y: { beginAtZero: true, ticks: { color: '#a1a1b5', callback: function (v) { return v + ' %'; } }, grid: { color: 'rgba(255,255,255,.06)' } }
            }
        }
    });
})();
