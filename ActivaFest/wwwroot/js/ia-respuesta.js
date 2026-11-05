// =============================
// 🤖 PIEZAS DE LAS RESPUESTAS DE LA IA (tabla, gráfico y recomendaciones)
// =============================
// Las comparten el chat, el panel "Pregúntale a la IA" y el dashboard, para que todos se vean igual.
// Todo se pinta con textContent (nunca innerHTML): ni la pregunta ni la respuesta pueden inyectar HTML.
window.IaRespuesta = (function () {
    'use strict';

    // Paleta categórica validada, en orden fijo; el resto de una torta se agrupa en "Otros"
    const PALETA = ['#3987e5', '#d95926', '#199e70', '#c98500', '#d55181', '#008300', '#9085e9', '#e66767'];
    const COLOR_OTROS = '#6b6b7b';
    const MAXIMO_PORCIONES = 6;
    const ICONOS = { Alta: '⚠', Media: '●', Oportunidad: '↗' };

    const TEXTO = '#c3c2b7', TEXTO_SUAVE = '#8f8e86', GRILLA = '#2c2c3a';

    function elemento(tag, clase, texto) {
        const e = document.createElement(tag);
        if (clase) e.className = clase;
        if (texto !== undefined) e.textContent = texto;
        return e;
    }

    // Chart.js solo se descarga si una respuesta trae un gráfico (misma versión e integridad que _ChartJs.cshtml)
    let cargaChart = null;
    function conChart() {
        if (window.Chart) return Promise.resolve();
        if (!cargaChart) {
            cargaChart = new Promise(function (listo, fallo) {
                const s = document.createElement('script');
                s.src = 'https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.min.js';
                s.integrity = 'sha384-9nhczxUqK87bcKHh20fSQcTGD4qq5GhayNYSYWqwBkINBhOfQLg/P5HG5lF1urn4';
                s.crossOrigin = 'anonymous';
                s.onload = listo;
                s.onerror = fallo;
                document.head.appendChild(s);
            });
        }
        return cargaChart;
    }

    // Una torta se lee hasta con 6 porciones: de mayor a menor y lo demás en "Otros"
    function plegar(items) {
        const orden = items.filter(function (i) { return i.valor > 0; }).sort(function (a, b) { return b.valor - a.valor; });
        if (orden.length <= MAXIMO_PORCIONES) return orden;
        const visibles = orden.slice(0, MAXIMO_PORCIONES - 1);
        const resto = orden.slice(MAXIMO_PORCIONES - 1).reduce(function (s, i) { return s + i.valor; }, 0);
        return visibles.concat([{ etiqueta: 'Otros', valor: resto, color: COLOR_OTROS }]);
    }

    function porcentaje(valor, valores) {
        const total = valores.reduce(function (s, v) { return s + v; }, 0);
        return total === 0 ? '0 %' : (100 * valor / total).toLocaleString('es-PE', { maximumFractionDigits: 1 }) + ' %';
    }

    function opcionesTorta(formato) {
        return {
            responsive: true, maintainAspectRatio: false,
            plugins: {
                legend: { position: 'bottom', labels: { color: TEXTO, boxWidth: 12, boxHeight: 12 } },
                tooltip: {
                    callbacks: {
                        label: function (c) { return `${c.label}: ${formato(c.parsed)} (${porcentaje(c.parsed, c.dataset.data)})`; }
                    }
                }
            }
        };
    }

    function configuracion(g) {
        const numero = function (v) { return Number(v).toLocaleString('es-PE', { maximumFractionDigits: 2 }); };

        // Área polar: como una torta, pero el radio es el que indica el valor
        if (g.tipo === 'polarArea') {
            const partes = plegar(g.etiquetas.map(function (e, i) { return { etiqueta: e, valor: g.valores[i] }; }));
            return {
                type: 'polarArea',
                data: {
                    labels: partes.map(function (i) { return i.etiqueta; }),
                    datasets: [{ label: g.serie, data: partes.map(function (i) { return i.valor; }), backgroundColor: partes.map(function (x, i) { return (x.color || PALETA[i]) + 'cc'; }), borderColor: '#1a1a25', borderWidth: 2 }]
                },
                options: {
                    responsive: true, maintainAspectRatio: false,
                    plugins: { legend: { position: 'bottom', labels: { color: TEXTO, boxWidth: 12, boxHeight: 12 } } },
                    scales: { r: { beginAtZero: true, grid: { color: GRILLA }, ticks: { display: false } } }
                }
            };
        }

        if (g.tipo === 'radar') {
            return {
                type: 'radar',
                data: {
                    labels: g.etiquetas,
                    datasets: [{ label: g.serie, data: g.valores, borderColor: PALETA[0], backgroundColor: PALETA[0] + '33', borderWidth: 2, pointBackgroundColor: PALETA[0], pointRadius: 4 }]
                },
                options: {
                    responsive: true, maintainAspectRatio: false,
                    plugins: { legend: { display: false } },
                    scales: { r: { beginAtZero: true, grid: { color: GRILLA }, angleLines: { color: GRILLA }, pointLabels: { color: TEXTO }, ticks: { color: TEXTO_SUAVE, backdropColor: 'transparent' } } }
                }
            };
        }

        if (g.tipo === 'stacked' && g.series) {
            const eje = { stacked: true, ticks: { color: TEXTO_SUAVE }, grid: { color: GRILLA }, border: { display: false } };
            return {
                type: 'bar',
                data: {
                    labels: g.etiquetas,
                    datasets: g.series.map(function (s, i) { return { label: s.nombre, data: s.valores, backgroundColor: PALETA[i], maxBarThickness: 32, borderColor: '#1a1a25', borderWidth: 1 }; })
                },
                options: {
                    responsive: true, maintainAspectRatio: false,
                    interaction: { mode: 'index', intersect: false },
                    plugins: { legend: { position: 'bottom', labels: { color: TEXTO, boxWidth: 12, boxHeight: 12 } } },
                    scales: { x: Object.assign({}, eje, { grid: { display: false } }), y: Object.assign({ beginAtZero: true }, eje) }
                }
            };
        }

        if (g.tipo === 'pie' || g.tipo === 'doughnut') {
            const items = plegar(g.etiquetas.map(function (e, i) { return { etiqueta: e, valor: g.valores[i] }; }));
            return {
                type: g.tipo,
                data: {
                    labels: items.map(function (i) { return i.etiqueta; }),
                    datasets: [{
                        label: g.serie,
                        data: items.map(function (i) { return i.valor; }),
                        backgroundColor: items.map(function (x, i) { return x.color || PALETA[i]; }),
                        borderColor: '#1a1a25', borderWidth: items.length > 1 ? 2 : 0
                    }]
                },
                options: opcionesTorta(numero)
            };
        }

        const horizontal = g.tipo === 'barh';
        const area = g.tipo === 'area';
        const puntos = g.tipo === 'scatter' || g.tipo === 'bubble';
        const linea = g.tipo === 'line' || area || puntos;

        // Burbujas: el tamaño de cada punto sigue a su valor
        const mayor = Math.max.apply(null, g.valores.concat([1]));
        const radios = g.tipo === 'bubble' ? g.valores.map(function (v) { return 4 + 16 * v / mayor; })
            : puntos ? 6 : (g.etiquetas.length > 14 ? 0 : 4);   // con muchos días, los puntos taparían la línea
        const eje = { beginAtZero: true, ticks: { color: TEXTO_SUAVE }, grid: { color: GRILLA }, border: { display: false } };
        const categorias = { ticks: { color: TEXTO_SUAVE, maxRotation: 30 }, grid: { display: false }, border: { display: false } };
        return {
            type: linea ? 'line' : 'bar',
            data: {
                labels: g.etiquetas,
                datasets: [{
                    label: g.serie, data: g.valores, backgroundColor: area ? PALETA[0] + '40' : PALETA[0], borderColor: PALETA[0], fill: area, showLine: !puntos,
                    borderRadius: linea ? 0 : 4, maxBarThickness: 32, borderWidth: linea ? 2 : 0, pointRadius: linea ? radios : 0, pointHoverRadius: 5, pointBackgroundColor: PALETA[0], tension: 0
                }]
            },
            options: {
                indexAxis: horizontal ? 'y' : 'x',
                responsive: true, maintainAspectRatio: false,
                interaction: { mode: 'index', axis: horizontal ? 'y' : 'x', intersect: false },
                plugins: { legend: { display: false } },
                scales: horizontal ? { x: eje, y: categorias } : { x: categorias, y: eje }
            }
        };
    }

    function grafico(g) {
        const caja = elemento('div', 'ia-grafico-caja');
        caja.appendChild(elemento('p', 'ia-grafico-titulo', g.titulo || g.serie));
        const lienzo = elemento('div', 'grafico-chart ia-grafico');
        const canvas = elemento('canvas');
        canvas.setAttribute('role', 'img');
        canvas.setAttribute('aria-label', g.titulo || g.serie);
        lienzo.appendChild(canvas);
        caja.appendChild(lienzo);

        conChart()
            .then(function () { requestAnimationFrame(function () { new Chart(canvas, configuracion(g)); }); })
            .catch(function () { lienzo.replaceChildren(elemento('p', 'texto-tenue', 'No se pudo cargar el gráfico; las cifras están en la tabla.')); });
        return caja;
    }

    function tabla(t) {
        const caja = elemento('div', 'tabla-contenedor');
        const table = elemento('table', 'tabla-datos ia-tabla');
        table.appendChild(elemento('caption', 'texto-tenue', t.titulo));
        const fila = elemento('tr');
        t.columnas.forEach(function (c) { fila.appendChild(elemento('th', null, c)); });
        table.appendChild(elemento('thead')).appendChild(fila);
        const cuerpo = elemento('tbody');
        if (t.filas.length === 0) {
            const vacia = elemento('td', 'texto-tenue', 'Sin datos para este período.');
            vacia.colSpan = t.columnas.length;
            cuerpo.appendChild(elemento('tr')).appendChild(vacia);
        }
        t.filas.forEach(function (f) {
            const tr = elemento('tr');
            f.forEach(function (v) { tr.appendChild(elemento('td', null, v)); });
            cuerpo.appendChild(tr);
        });
        table.appendChild(cuerpo);
        caja.appendChild(table);
        return caja;
    }

    // Prioridad con ícono y texto, nunca solo color
    function recomendacion(r) {
        const caja = elemento('article', 'recomendacion recomendacion-' + r.prioridad.toLowerCase());
        caja.appendChild(elemento('span', 'recomendacion-prioridad', `${ICONOS[r.prioridad] || ''} ${r.prioridad}`));
        caja.appendChild(elemento('h4', null, r.hallazgo));
        caja.appendChild(elemento('p', 'recomendacion-dato', r.dato));
        caja.appendChild(elemento('p', 'recomendacion-accion', r.accion));
        return caja;
    }

    // Agrega a "caja" lo que traiga la respuesta, además de su texto
    function completar(caja, r) {
        if (r.grafico) caja.appendChild(grafico(r.grafico));
        if (r.tabla) caja.appendChild(tabla(r.tabla));
        if (r.recomendaciones && r.recomendaciones.length) {
            caja.appendChild(elemento('p', 'ia-subtitulo', '💡 Recomendaciones'));
            const lista = elemento('div', 'lista-recomendaciones');
            r.recomendaciones.forEach(function (x) { lista.appendChild(recomendacion(x)); });
            caja.appendChild(lista);
        }
    }

    return {
        PALETA: PALETA, elemento: elemento, plegar: plegar, opcionesTorta: opcionesTorta, configuracion: configuracion,
        recomendacion: recomendacion, completar: completar
    };
})();
