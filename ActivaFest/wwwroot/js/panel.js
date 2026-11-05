// =============================
// 📊 GRÁFICOS DEL PANEL DE VENTAS CON CHART.JS (HU13)
// =============================
// Los datos vienen del servidor en <script type="application/json" id="datosPanel"> (las mismas cifras de las
// tablas "Ver como tabla"). Si Chart.js no carga, las tablas siguen mostrando todo.
(function () {
    'use strict';

    const fuente = document.getElementById('datosPanel');
    if (!fuente || typeof Chart === 'undefined') return;

    const datos = JSON.parse(fuente.textContent);
    const estilo = getComputedStyle(document.documentElement);
    const color = (nombre, defecto) => (estilo.getPropertyValue(nombre) || defecto).trim();

    const MORADO = color('--morado', '#8b5cf6');
    const CIAN = color('--cian', '#06b6d4');
    const TEXTO = color('--texto-2', '#a1a1aa');
    const BORDE = 'rgba(255, 255, 255, 0.08)';
    const soles = (v) => 'S/ ' + Number(v).toLocaleString('es-PE', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

    Chart.defaults.color = TEXTO;
    Chart.defaults.font.family = color('--fuente', 'Outfit, sans-serif');
    Chart.defaults.borderColor = BORDE;

    // ---------- Entradas vendidas (barras) e ingresos (línea) por día ----------
    const lienzoVentas = document.getElementById('graficoVentas');
    if (lienzoVentas && datos.dias) {
        new Chart(lienzoVentas, {
            data: {
                labels: datos.dias.map(d => d.etiqueta),
                datasets: [
                    { type: 'bar', label: 'Entradas', data: datos.dias.map(d => d.entradas), backgroundColor: MORADO, borderRadius: 4, maxBarThickness: 24, yAxisID: 'y' },
                    { type: 'line', label: 'Ingresos', data: datos.dias.map(d => d.ingresos), borderColor: CIAN, backgroundColor: CIAN, tension: 0, pointRadius: 2, pointHoverRadius: 5, yAxisID: 'y1' }
                ]
            },
            options: {
                maintainAspectRatio: false,
                interaction: { mode: 'index', intersect: false },
                plugins: {
                    legend: { position: 'bottom' },
                    tooltip: { callbacks: { label: c => c.dataset.yAxisID === 'y1' ? `Ingresos: ${soles(c.parsed.y)}` : `Entradas: ${c.parsed.y}` } }
                },
                scales: {
                    x: { grid: { display: false }, ticks: { maxTicksLimit: 10 } },
                    y: { beginAtZero: true, ticks: { precision: 0 }, title: { display: true, text: 'Entradas' } },
                    y1: { beginAtZero: true, position: 'right', grid: { drawOnChartArea: false }, ticks: { callback: v => 'S/ ' + v } }
                }
            }
        });
    }

    // ---------- Aforo por zona: vendidas, en reserva y disponibles (barras apiladas) ----------
    const lienzoZonas = document.getElementById('graficoZonas');
    if (lienzoZonas && datos.zonas) {
        new Chart(lienzoZonas, {
            type: 'bar',
            data: {
                labels: datos.zonas.map(z => z.nombre),
                datasets: [
                    { label: 'Vendidas', data: datos.zonas.map(z => z.vendidas), backgroundColor: MORADO, borderRadius: 4 },
                    { label: 'En reserva', data: datos.zonas.map(z => z.reservadas), backgroundColor: '#f59e0b', borderRadius: 4 },
                    { label: 'Disponibles', data: datos.zonas.map(z => z.disponibles), backgroundColor: 'rgba(255, 255, 255, 0.14)', borderRadius: 4 }
                ]
            },
            options: {
                indexAxis: 'y',
                maintainAspectRatio: false,
                plugins: { legend: { position: 'bottom' } },
                scales: { x: { stacked: true, beginAtZero: true, ticks: { precision: 0 } }, y: { stacked: true, grid: { display: false } } }
            }
        });
    }

    // ---------- Panel general: vendidas y aforo restante por evento ----------
    const lienzoEventos = document.getElementById('graficoEventos');
    if (lienzoEventos && datos.eventos) {
        new Chart(lienzoEventos, {
            type: 'bar',
            data: {
                labels: datos.eventos.map(e => e.titulo),
                datasets: [
                    { label: 'Vendidas', data: datos.eventos.map(e => e.vendidas), backgroundColor: MORADO, borderRadius: 4 },
                    { label: 'Aforo restante', data: datos.eventos.map(e => e.restante), backgroundColor: 'rgba(255, 255, 255, 0.14)', borderRadius: 4 }
                ]
            },
            options: {
                indexAxis: 'y',
                maintainAspectRatio: false,
                plugins: {
                    legend: { position: 'bottom' },
                    tooltip: { callbacks: { afterBody: items => 'Ingresos: ' + soles(datos.eventos[items[0].dataIndex].ingresos) } }
                },
                scales: { x: { stacked: true, beginAtZero: true, ticks: { precision: 0 } }, y: { stacked: true, grid: { display: false } } }
            }
        });
    }
})();
