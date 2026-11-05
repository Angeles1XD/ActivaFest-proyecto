// =============================
// 📈 ANÁLISIS DE VENTAS DEL DASHBOARD
// =============================
// Pide una vez las cifras a /Analitica/Analisis o /Panel/Analisis (los filtros recargan la página) y pinta gráficos,
// tablas y recomendaciones. Cada página solo tiene sus secciones: lo que no está en la página se salta.
// Todo el texto va con textContent: los nombres de eventos nunca se interpretan como HTML.
(function () {
    'use strict';

    const raiz = document.getElementById('analisis');
    if (!raiz) return;

    const soles = v => 'S/ ' + Number(v).toLocaleString('es-PE', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const solesCorto = v => 'S/ ' + Number(v).toLocaleString('es-PE', { maximumFractionDigits: 0 });
    const entero = v => Number(v).toLocaleString('es-PE');
    const pct = v => v === null || v === undefined ? '—' : Number(v).toLocaleString('es-PE', { maximumFractionDigits: 1 }) + ' %';
    const $ = id => document.getElementById(id);

    const estilo = getComputedStyle(raiz);
    const token = (nombre, defecto) => (estilo.getPropertyValue(nombre) || defecto).trim();
    const SERIE = token('--viz-serie', '#3987e5');
    const GRILLA = token('--viz-grilla', '#2c2c3a');
    const SIN_ENFASIS = '#4b4b5e';   // resalta una barra: el resto queda en gris

    const elemento = IaRespuesta.elemento;

    // Una sola serie: un color, sin leyenda (el título dice qué se grafica). El detalle exacto va en el tooltip y en "Ver tabla".
    function barras(id, etiquetas, valores, o) {
        const lienzo = $(id);
        if (!lienzo) return;
        const vacio = !valores.some(v => v > 0);
        lienzo.parentElement.hidden = vacio;
        $(id + 'Vacio').hidden = !vacio;
        if (vacio || typeof Chart === 'undefined') return;

        const horizontal = !!o.horizontal;
        const eje = {
            beginAtZero: true,
            grid: { color: GRILLA },
            border: { display: false },
            ticks: o.enteros ? { precision: 0 } : { callback: v => (o.eje || solesCorto)(v) }
        };
        const categorias = {
            grid: { display: false },
            border: { display: false },
            ticks: {
                autoSkip: !horizontal,
                callback: function (v) {
                    const texto = this.getLabelForValue(v);
                    return texto.length > 24 ? texto.slice(0, 23) + '…' : texto;
                }
            }
        };
        const mayor = valores.indexOf(Math.max.apply(null, valores));

        new Chart(lienzo, {
            type: 'bar',
            data: {
                labels: etiquetas,
                datasets: [{
                    label: o.serie, data: valores, borderRadius: 4, maxBarThickness: 32,
                    backgroundColor: o.resaltar ? valores.map((_, i) => i === mayor ? SERIE : SIN_ENFASIS) : SERIE
                }]
            },
            options: {
                indexAxis: horizontal ? 'y' : 'x',
                maintainAspectRatio: false,
                interaction: { mode: 'index', axis: horizontal ? 'y' : 'x', intersect: false },
                plugins: {
                    legend: { display: false },
                    tooltip: { callbacks: { label: c => `${o.serie}: ${o.formato(horizontal ? c.parsed.x : c.parsed.y)}` } }
                },
                scales: horizontal ? { x: eje, y: categorias } : { x: categorias, y: eje }
            }
        });
    }

    // Torta: partes de un todo, con leyenda siempre visible; el color sigue a cada elemento, no a su posición
    function torta(id, items, tipo, formato) {
        const lienzo = $(id);
        if (!lienzo) return;
        const partes = IaRespuesta.plegar(items);
        const vacio = partes.length === 0;
        lienzo.parentElement.hidden = vacio;
        $(id + 'Vacio').hidden = !vacio;
        if (vacio || typeof Chart === 'undefined') return;

        new Chart(lienzo, {
            type: tipo,
            data: {
                labels: partes.map(p => p.etiqueta),
                datasets: [{ data: partes.map(p => p.valor), backgroundColor: partes.map(p => p.color), borderColor: token('--viz-superficie', '#1a1a25'), borderWidth: partes.length > 1 ? 2 : 0 }]
            },
            options: IaRespuesta.opcionesTorta(formato)
        });
    }

    // Los demás tipos usan la misma configuración que los gráficos que dibuja la IA
    function grafico(id, g) {
        const lienzo = $(id);
        if (!lienzo) return;
        const vacio = !(g.series ? g.series.flatMap(s => s.valores) : g.valores).some(v => v > 0);
        lienzo.parentElement.hidden = vacio;
        $(id + 'Vacio').hidden = !vacio;
        if (vacio || typeof Chart === 'undefined') return;
        new Chart(lienzo, IaRespuesta.configuracion(g));
    }

    // Tres medidas a la vez: precio (x), ocupación (y) e ingresos (tamaño de la burbuja)
    function burbujas(id, eventos) {
        const lienzo = $(id);
        if (!lienzo) return;
        const vacio = eventos.length === 0;
        lienzo.parentElement.hidden = vacio;
        $(id + 'Vacio').hidden = !vacio;
        if (vacio || typeof Chart === 'undefined') return;

        const mayor = Math.max.apply(null, eventos.map(e => e.ingresos));
        const eje = titulo => ({ beginAtZero: true, grid: { color: GRILLA }, border: { display: false }, title: { display: true, text: titulo } });
        new Chart(lienzo, {
            type: 'bubble',
            data: {
                datasets: [{
                    label: 'Eventos', backgroundColor: SERIE + '99', borderColor: SERIE,
                    data: eventos.map(e => ({ x: e.ingresos / e.entradas, y: e.ocupacion, r: 5 + 18 * e.ingresos / mayor, evento: e.evento, ingresos: e.ingresos }))
                }]
            },
            options: {
                maintainAspectRatio: false,
                plugins: {
                    legend: { display: false },
                    tooltip: { callbacks: { label: c => `${c.raw.evento}: ${soles(c.raw.x)} promedio · ${pct(c.raw.y)} de ocupación · ${soles(c.raw.ingresos)}` } }
                },
                scales: { x: eje('Precio promedio (S/)'), y: eje('Ocupación (%)') }
            }
        });
    }

    function pintarComparativas(a) {
        const fechas = a.ventasDiarias.map(d => d.etiqueta);
        grafico('graficoAcumulado', { tipo: 'area', serie: 'Ingresos acumulados', etiquetas: fechas, valores: a.ventasDiarias.map(d => d.acumulado) });
        verTabla('tablaAcumulado', ['Fecha', 'Ingresos del día', 'Acumulado'],
            a.ventasDiarias.filter(d => d.ingresos > 0).map(d => [d.etiqueta, '>' + soles(d.ingresos), '>' + soles(d.acumulado)]));

        grafico('graficoNetoComision', {
            tipo: 'stacked', serie: 'Ingresos', etiquetas: a.meses.map(m => m.etiqueta), valores: a.meses.map(m => m.ingresos),
            series: [
                { nombre: 'Neto', valores: a.meses.map(m => m.ingresos - m.comisiones) },
                { nombre: 'Comisión', valores: a.meses.map(m => m.comisiones) }
            ]
        });

        barras('graficoHoras', a.comprasPorHora.map(h => h.hora), a.comprasPorHora.map(h => h.compras), { serie: 'Compras', formato: entero, enteros: true, resaltar: true });
        verTabla('tablaHoras', ['Hora', 'Compras'], a.comprasPorHora.filter(h => h.compras > 0).map(h => [h.hora, '>' + entero(h.compras)]));

        grafico('radarDias', { tipo: 'radar', serie: 'Ingresos', etiquetas: a.diasSemana.map(d => d.dia), valores: a.diasSemana.map(d => d.ingresos) });
        grafico('polarZonas', { tipo: 'polarArea', serie: 'Ingresos', etiquetas: a.porZona.map(z => z.zona), valores: a.porZona.map(z => z.ingresos) });

        const conOcupacion = a.topEventos.filter(e => e.ocupacion !== null && e.entradas > 0);
        burbujas('burbujasEventos', conOcupacion);
        verTabla('tablaBurbujas', ['Evento', 'Precio promedio', 'Ocupación', 'Ingresos'],
            conOcupacion.map(e => [e.evento, '>' + soles(e.ingresos / e.entradas), '>' + pct(e.ocupacion), '>' + soles(e.ingresos)]));

        const ordenes = a.ordenesPorEstado.reduce((s, o) => s + o.total, 0);
        barras('graficoEmbudo', ['Visitas a las fichas', 'Órdenes creadas', 'Compras pagadas'], [a.visitasEventos, ordenes, a.compras],
            { serie: 'Cantidad', formato: entero, enteros: true, horizontal: true });
    }

    function pintarDistribucion(a) {
        const paleta = IaRespuesta.PALETA;
        torta('tortaCategorias',
            a.porCategoria.map(c => ({ etiqueta: c.categoria, valor: c.ingresos, color: paleta[(c.categoriaId - 1) % paleta.length] })), 'pie', soles);
        verTabla('tablaTortaCategorias', ['Categoría', 'Ingresos'], a.porCategoria.map(c => [c.categoria, '>' + soles(c.ingresos)]));

        torta('tortaEstados', a.eventosPorEstado.map((e, i) => ({ etiqueta: e.etiqueta, valor: e.total, color: paleta[i] })), 'doughnut', entero);
        verTabla('tablaEstados', ['Estado', 'Eventos'], a.eventosPorEstado.map(e => [e.etiqueta, '>' + entero(e.total)]));

        barras('graficoZonas', a.porZona.map(z => z.zona), a.porZona.map(z => z.entradas), { serie: 'Entradas', formato: entero, enteros: true, horizontal: true });
        verTabla('tablaZonas', ['Zona', 'Entradas', 'Ingresos'], a.porZona.map(z => [z.zona, '>' + entero(z.entradas), '>' + soles(z.ingresos)]));

        const ocupacion = a.topEventos.filter(e => e.ocupacion !== null).sort((x, y) => y.ocupacion - x.ocupacion);
        barras('graficoOcupacion', ocupacion.map(e => e.evento), ocupacion.map(e => e.ocupacion), { serie: 'Ocupación', formato: pct, eje: pct, horizontal: true });
        verTabla('tablaOcupacion', ['Evento', 'Entradas', 'Ocupación'], ocupacion.map(e => [e.evento, '>' + entero(e.entradas), '>' + pct(e.ocupacion)]));

        barras('graficoOrganizadores', a.topOrganizadores.map(o => o.organizador), a.topOrganizadores.map(o => o.ingresos), { serie: 'Ingresos', formato: soles, horizontal: true });
        verTabla('tablaOrganizadores', ['Organizador', 'Eventos', 'Ingresos'], a.topOrganizadores.map(o => [o.organizador, '>' + entero(o.eventos), '>' + soles(o.ingresos)]));

        barras('graficoOrdenes', a.ordenesPorEstado.map(o => o.etiqueta), a.ordenesPorEstado.map(o => o.total), { serie: 'Órdenes', formato: entero, enteros: true });
        verTabla('tablaOrdenes', ['Estado', 'Órdenes'], a.ordenesPorEstado.map(o => [o.etiqueta, '>' + entero(o.total)]));
    }

    // celdas: texto o un nodo (enlaces). Las columnas que empiezan con ">" van alineadas a la derecha.
    function tabla(cuerpo, filas, columnas, textoVacio) {
        if (!cuerpo) return;
        cuerpo.replaceChildren();
        if (filas.length === 0) {
            const celda = cuerpo.insertRow().insertCell();
            celda.colSpan = columnas;
            celda.className = 'texto-tenue';
            celda.textContent = textoVacio;
        }
        filas.forEach(f => {
            const fila = cuerpo.insertRow();
            f.forEach(v => {
                const celda = fila.insertCell();
                if (v instanceof Node) {
                    celda.appendChild(v);
                } else {
                    const derecha = typeof v === 'string' && v.startsWith('>');
                    celda.textContent = derecha ? v.slice(1) : v;
                    if (derecha) celda.className = 'derecha num';
                }
            });
        });
    }

    // Vista de tabla de cada gráfico: el dato nunca depende solo del color o del tooltip
    function verTabla(id, encabezados, filas) {
        if (!$(id)) return;
        const tablaHtml = elemento('table', 'tabla-datos');
        const cabecera = tablaHtml.createTHead().insertRow();
        encabezados.forEach((t, i) => {
            const th = elemento('th', i > 0 ? 'derecha' : '', t);
            cabecera.appendChild(th);
        });
        tabla(tablaHtml.createTBody(), filas, encabezados.length, 'Sin datos en este período.');
        const caja = elemento('div', 'tabla-contenedor');
        caja.appendChild(tablaHtml);
        $(id).appendChild(caja);
    }

    function enlaceEvento(id, titulo) {
        const a = elemento('a', '', titulo);
        a.href = raiz.dataset.urlEvento + '/' + encodeURIComponent(id);
        return a;
    }

    function pintarAcumulado(t) {
        const pone = (clave, valor) => {
            const dato = raiz.querySelector(`[data-acumulado="${clave}"]`);
            if (dato) dato.textContent = valor;
        };
        pone('ingresos', soles(t.ingresos));
        pone('comisiones', soles(t.comisiones));
        pone('entradas', entero(t.entradas));
        pone('compras', entero(t.compras));
        pone('compradores', entero(t.compradores));
        pone('usuarios', `de ${entero(t.usuariosRegistrados)} usuarios registrados`);
        pone('eventos', entero(t.eventosPublicados));
    }

    function pintarVentasEnElTiempo(a) {
        const meses = a.meses.map(m => m.etiqueta);
        barras('graficoMeses', meses, a.meses.map(m => m.ingresos), { serie: 'Ingresos', formato: soles });
        barras('graficoComisionesMes', meses, a.meses.map(m => m.comisiones), { serie: 'Comisiones', formato: soles });
        verTabla('tablaMeses', ['Mes', 'Ingresos', 'Comisiones'], a.meses.map(m => [m.etiqueta, '>' + soles(m.ingresos), '>' + soles(m.comisiones)]));

        barras('graficoDias', a.diasSemana.map(d => d.dia), a.diasSemana.map(d => d.compras), { serie: 'Compras', formato: entero, enteros: true, resaltar: true });
        verTabla('tablaDias', ['Día', 'Compras', 'Ingresos'], a.diasSemana.map(d => [d.dia, '>' + entero(d.compras), '>' + soles(d.ingresos)]));

        const categorias = a.porCategoria.slice().sort((x, y) => y.comisiones - x.comisiones);
        barras('graficoComisionesCategoria', categorias.map(c => c.categoria), categorias.map(c => c.comisiones), { serie: 'Comisiones', formato: soles, horizontal: true });
        verTabla('tablaCategorias', ['Categoría', 'Ingresos', 'Comisiones'], categorias.map(c => [c.categoria, '>' + soles(c.ingresos), '>' + soles(c.comisiones)]));
    }

    function pintarEventos(a) {
        barras('graficoTopEventos', a.topEventos.map(e => e.evento), a.topEventos.map(e => e.ingresos), { serie: 'Ingresos', formato: soles, horizontal: true });
        tabla(document.querySelector('#tablaTopEventos tbody'), a.topEventos.map((e, i) => [
            String(i + 1), enlaceEvento(e.eventoId, e.evento), e.categoria + (e.destacado ? ' · ⭐ Destacado' : ''),
            '>' + entero(e.entradas), '>' + soles(e.ingresos), '>' + soles(e.comision), '>' + pct(e.ocupacion)
        ]), 7, 'No hay eventos con ventas en este período.');

        tabla(document.querySelector('#tablaDestacados tbody'), a.destacados.map(d => [
            enlaceEvento(d.eventoId, d.evento), d.paquete, d.vigencia, '>' + soles(d.precioPaquete), '>' + entero(d.entradas), '>' + soles(d.ingresos)
        ]), 6, 'Ningún evento tuvo un paquete destacado vigente en este período.');
    }

    function pintarCompradores(a) {
        $('compradoresTotal').textContent = entero(a.compradores);
        $('compradoresNuevos').textContent = entero(a.compradoresNuevos);
        $('compradoresRecurrentes').textContent = entero(a.compradoresRecurrentes);
        $('compradoresTicket').textContent = soles(a.ticketPromedio);

        const barra = $('barraCompradores');
        if (barra) {
            barra.hidden = a.compradores === 0;
            barra.querySelector('.segmento-nuevos').style.flexGrow = a.compradoresNuevos;
            barra.querySelector('.segmento-recurrentes').style.flexGrow = a.compradoresRecurrentes;
        }

        const segmentos = a.segmentos.slice().sort((x, y) => y.clientes - x.clientes);
        barras('graficoSegmentos', segmentos.map(s => s.segmento), segmentos.map(s => s.clientes), { serie: 'Clientes', formato: entero, enteros: true, horizontal: true });
        verTabla('tablaSegmentos', ['Segmento', 'Clientes', 'Gasto total', 'Aceptan promociones'],
            segmentos.map(s => [s.segmento, '>' + entero(s.clientes), '>' + soles(s.gastoTotal), '>' + entero(s.conConsentimiento)]));

        tabla(document.querySelector('#tablaCompradores tbody'), a.topCompradores.map(c => [
            'Cliente ' + c.cliente, c.segmento, '>' + entero(c.compras), '>' + entero(c.entradas), '>' + soles(c.gasto)
        ]), 5, 'No hay compradores en este período.');
    }

    function pintarRecomendaciones(lista) {
        const caja = $('listaRecomendaciones');
        caja.replaceChildren();
        lista.forEach(r => caja.appendChild(IaRespuesta.recomendacion(r)));
    }

    // Indicadores del panel del organizador
    function pintarResumen(a) {
        const pone = (id, valor) => { if ($(id)) $(id).textContent = valor; };
        pone('analisisIngresos', soles(a.ingresos));
        pone('analisisNeto', soles(a.ingresos - a.comisiones));
        pone('analisisEntradas', entero(a.entradas));
    }

    // Filtros del panel del organizador: se aplican al elegirlos, igual que en el dashboard
    const filtros = $('filtrosAnalisis');
    if (filtros) {
        const rango = $('rango');
        const fechas = filtros.querySelectorAll('.filtro-fecha');
        const mostrarFechas = () => {
            const personalizado = rango.value === 'personalizado';
            fechas.forEach(f => {
                f.classList.toggle('inactivo', !personalizado);
                f.querySelector('input').disabled = !personalizado;
            });
        };
        rango.addEventListener('change', () => {
            mostrarFechas();
            if (rango.value !== 'personalizado') filtros.submit();
        });
        $('catAnalisis').addEventListener('change', () => {
            $('eventoId').value = '';
            filtros.submit();
        });
        $('eventoId').addEventListener('change', () => filtros.submit());
        mostrarFechas();
    }

    fetch(raiz.dataset.url, { headers: { Accept: 'application/json' }, cache: 'no-store' })
        .then(r => { if (!r.ok) throw new Error(r.status); return r.json(); })
        .then(a => {
            pintarResumen(a);
            pintarAcumulado(a.acumulado);
            pintarVentasEnElTiempo(a);
            pintarDistribucion(a);
            pintarComparativas(a);
            pintarEventos(a);
            pintarCompradores(a);
            pintarRecomendaciones(a.recomendaciones);
            $('analisisEstado').hidden = true;
        })
        .catch(() => { $('analisisEstado').textContent = 'No se pudo cargar el análisis. Recarga la página para intentarlo de nuevo.'; });
})();
