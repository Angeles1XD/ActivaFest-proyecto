// =============================
// 📊 DASHBOARD DEL ADMINISTRADOR (HU21): gráficos, filtro de período y actualización en vivo
// =============================
// Cada 20 segundos (solo con la pestaña visible) vuelve a pedir los datos a /Analitica/Datos y actualiza
// indicadores, gráficos y últimas compras sin recargar la página (no se pierde lo escrito en el panel de la IA).
(function () {
    'use strict';

    const raiz = document.getElementById('dashboard');
    const fuente = document.getElementById('datosDashboard');
    if (!raiz || !fuente) return;

    const INTERVALO = 20000;
    const soles = v => 'S/ ' + Number(v).toLocaleString('es-PE', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const entero = v => Number(v).toLocaleString('es-PE');
    const pct = v => v === null || v === undefined ? '—' : Number(v).toLocaleString('es-PE', { maximumFractionDigits: 1 }) + ' %';

    // ---------- Filtro: el período rápido y las fechas personalizadas ----------
    const formulario = document.getElementById('filtrosDashboard');
    const rango = document.getElementById('rango');
    const fechas = formulario.querySelectorAll('.filtro-fecha input');
    function mostrarFechas() {
        const personalizado = rango.value === 'personalizado';
        formulario.querySelectorAll('.filtro-fecha').forEach(f => f.classList.toggle('inactivo', !personalizado));
        fechas.forEach(i => { i.disabled = !personalizado; });
    }
    rango.addEventListener('change', function () {
        mostrarFechas();
        if (rango.value !== 'personalizado') formulario.submit();   // los períodos rápidos se aplican al elegirlos
    });
    formulario.querySelector('#categoriaId').addEventListener('change', function () {
        formulario.querySelector('#eventoId').value = '';   // al cambiar de categoría, el evento elegido puede no ser de ella
        formulario.submit();
    });
    formulario.querySelector('#eventoId').addEventListener('change', () => formulario.submit());
    mostrarFechas();

    // ---------- Gráficos ----------
    const datos = JSON.parse(fuente.textContent);
    const estilo = getComputedStyle(document.documentElement);
    const color = (n, d) => (estilo.getPropertyValue(n) || d).trim();
    const MORADO = color('--morado', '#8b5cf6');
    let graficoSemanas = null, graficoCategorias = null;

    if (typeof Chart !== 'undefined') {
        Chart.defaults.color = color('--texto-2', '#a1a1aa');
        Chart.defaults.font.family = color('--fuente', 'Outfit, sans-serif');
        Chart.defaults.borderColor = 'rgba(255, 255, 255, 0.08)';

        graficoSemanas = new Chart(document.getElementById('graficoSemanas'), {
            type: 'line',
            data: {
                labels: datos.semanas.map(s => s.etiqueta),
                datasets: [{ label: 'Ingresos', data: datos.semanas.map(s => s.ingresos), borderColor: MORADO, backgroundColor: 'rgba(139, 92, 246, 0.18)', fill: true, tension: 0, pointRadius: 3 }]
            },
            options: {
                maintainAspectRatio: false,
                plugins: { legend: { display: false }, tooltip: { callbacks: { label: c => soles(c.parsed.y) } } },
                scales: { x: { grid: { display: false } }, y: { beginAtZero: true, ticks: { callback: v => 'S/ ' + v } } }
            }
        });

        graficoCategorias = new Chart(document.getElementById('graficoCategorias'), {
            type: 'bar',
            data: { labels: [], datasets: [{ label: 'Entradas', data: [], backgroundColor: [], borderRadius: 4 }] },
            options: {
                indexAxis: 'y',
                maintainAspectRatio: false,
                plugins: { legend: { display: false }, tooltip: { callbacks: { afterLabel: c => 'Ingresos: ' + soles(graficoCategorias.$ingresos[c.dataIndex]) } } },
                scales: { x: { beginAtZero: true, ticks: { precision: 0 } }, y: { grid: { display: false } } }
            }
        });
        pintarCategorias(datos.categorias);
    }

    function pintarCategorias(categorias) {
        document.getElementById('sinVentasCategoria').hidden = categorias.length > 0;
        document.getElementById('cajaCategorias').hidden = categorias.length === 0;
        if (!graficoCategorias) return;
        graficoCategorias.data.labels = categorias.map(c => c.nombre);
        graficoCategorias.data.datasets[0].data = categorias.map(c => c.entradas);
        graficoCategorias.data.datasets[0].backgroundColor = categorias.map(c => c.color || MORADO);
        graficoCategorias.$ingresos = categorias.map(c => c.ingresos);
        graficoCategorias.update();
    }

    // Filas de una tabla de compras; textContent: los datos nunca se interpretan como HTML
    function pintarCompras(cuerpo, compras, textoVacio) {
        cuerpo.replaceChildren();
        if (compras.length === 0) {
            const fila = cuerpo.insertRow();
            const celda = fila.insertCell();
            celda.colSpan = 5;
            celda.className = 'texto-tenue';
            celda.textContent = textoVacio;
        }
        compras.forEach(c => {
            const fila = cuerpo.insertRow();
            [c.fecha, c.evento, 'Cliente ' + c.cliente, entero(c.entradas), soles(c.total)].forEach((v, i) => {
                const celda = fila.insertCell();
                celda.textContent = v;
                celda.className = i === 1 ? '' : i >= 3 ? 'derecha num' : 'num';
            });
        });
    }

    // ---------- Ventas por semana, quincena, mes y trimestre ----------
    const seccionVentas = document.getElementById('ventasPeriodo');
    const tarjetas = seccionVentas.querySelectorAll('.tarjeta-periodo');
    const compras = n => entero(n) + (n === 1 ? ' compra' : ' compras');
    const ventas = { tipo: 'semana', fecha: null, pagina: 1, anterior: null, siguiente: null, pedido: 0 };
    const botonVentas = id => document.getElementById(id);

    async function cargarVentas() {
        const pedido = ++ventas.pedido;
        const url = new URL(seccionVentas.dataset.url, location.origin);
        url.searchParams.set('tipo', ventas.tipo);
        url.searchParams.set('pagina', ventas.pagina);
        if (ventas.fecha) url.searchParams.set('fecha', ventas.fecha);
        try {
            const r = await fetch(url, { headers: { Accept: 'application/json' }, cache: 'no-store' });
            const d = r.ok ? await r.json() : null;
            if (d && pedido === ventas.pedido) pintarVentas(d);   // una respuesta lenta no pisa a la última elegida
        } catch (e) { /* sin conexión: se reintenta en el siguiente ciclo */ }
    }

    function pintarVentas(d) {
        tarjetas.forEach(t => {
            const p = d.resumenes.find(x => x.tipo === t.dataset.tipo);
            const campo = nombre => t.querySelector(`[data-periodo="${nombre}"]`);
            destacar(campo('total'), soles(p.ingresos));
            campo('compras').textContent = compras(p.compras) + ' · ' + p.etiqueta;
            campo('anterior').textContent = 'Período anterior: ' + soles(p.ingresosAnterior);
            t.setAttribute('aria-pressed', String(t.dataset.tipo === d.tipo));
        });

        ventas.anterior = d.anterior;
        ventas.siguiente = d.siguiente;
        ventas.pagina = d.pagina;
        document.getElementById('ventasEtiqueta').textContent = d.etiqueta;
        document.getElementById('ventasFechas').textContent = `Del ${d.desde} al ${d.hasta}`;
        document.getElementById('ventasResumen').textContent =
            `${soles(d.ingresos)} en ${compras(d.compras)} · ${entero(d.entradas)} ${d.entradas === 1 ? 'entrada' : 'entradas'} · comisiones ${soles(d.comisiones)} · período anterior ${soles(d.ingresosAnterior)}`;
        botonVentas('ventasSiguiente').disabled = !d.siguiente;

        pintarCompras(document.querySelector('#tablaVentas tbody'), d.ventas, 'No hay compras pagadas en este período.');
        document.getElementById('ventasPaginas').hidden = d.paginas <= 1;
        document.getElementById('ventasPagina').textContent = `Página ${d.pagina} de ${d.paginas}`;
        botonVentas('ventasPaginaAnterior').disabled = d.pagina <= 1;
        botonVentas('ventasPaginaSiguiente').disabled = d.pagina >= d.paginas;
    }

    function irAVentas(cambios) {
        Object.assign(ventas, cambios);
        cargarVentas();
    }

    tarjetas.forEach(t => t.addEventListener('click', () => irAVentas({ tipo: t.dataset.tipo, fecha: null, pagina: 1 })));
    botonVentas('ventasAnterior').addEventListener('click', () => { if (ventas.anterior) irAVentas({ fecha: ventas.anterior, pagina: 1 }); });
    botonVentas('ventasSiguiente').addEventListener('click', () => { if (ventas.siguiente) irAVentas({ fecha: ventas.siguiente, pagina: 1 }); });
    botonVentas('ventasPaginaAnterior').addEventListener('click', () => irAVentas({ pagina: ventas.pagina - 1 }));
    botonVentas('ventasPaginaSiguiente').addEventListener('click', () => irAVentas({ pagina: ventas.pagina + 1 }));

    // ---------- Actualización en vivo ----------
    const kpi = clave => raiz.querySelector(`[data-kpi="${clave}"]`);
    const nota = clave => raiz.querySelector(`[data-nota="${clave}"]`);

    function variacion(elemento, v) {
        elemento.classList.toggle('sube', v !== null && v >= 0);
        elemento.classList.toggle('baja', v !== null && v < 0);
        elemento.textContent = v === null ? 'sin ventas en el período anterior'
            : (v >= 0 ? '▲ +' : '▼ ') + Number(v).toLocaleString('es-PE', { maximumFractionDigits: 1 }) + ' % vs. período anterior';
    }

    function destacar(elemento, texto) {
        if (elemento.textContent === texto) return;
        elemento.textContent = texto;
        elemento.classList.remove('cambio');
        void elemento.offsetWidth;            // reinicia la animación
        elemento.classList.add('cambio');
    }

    function pintar(d) {
        destacar(kpi('ingresos'), soles(d.ingresos));
        variacion(nota('ingresos'), d.ingresosVariacion);
        destacar(kpi('entradas'), entero(d.entradas));
        variacion(nota('entradas'), d.entradasVariacion);
        destacar(kpi('ticket'), soles(d.ticket));
        nota('ticket').textContent = entero(d.compras) + (d.compras === 1 ? ' compra' : ' compras');
        destacar(kpi('ocupacion'), pct(d.ocupacion));
        nota('ocupacion').textContent = `${entero(d.vendidasEventos)} de ${entero(d.aforoEventos)} lugares · ${d.eventos} ${d.eventos === 1 ? 'evento' : 'eventos'}`;
        destacar(kpi('asistencia'), pct(d.asistencia));
        nota('asistencia').textContent = d.realizados === 0 ? 'aún no se realiza ningún evento' : 'entradas validadas en puerta';
        destacar(kpi('comisiones'), soles(d.comisiones));
        destacar(kpi('conversion'), pct(d.conversion));
        destacar(kpi('activos'), entero(d.activos));

        if (graficoSemanas) {
            graficoSemanas.data.labels = d.semanas.map(s => s.etiqueta);
            graficoSemanas.data.datasets[0].data = d.semanas.map(s => s.ingresos);
            graficoSemanas.update();
        }
        pintarCategorias(d.categorias);

        pintarCompras(document.querySelector('#tablaUltimas tbody'), d.ultimas, 'Aún no hay compras pagadas en este período.');

        actualizado.dataset.calculado = d.calculadoEn;
        reloj();
    }

    const actualizado = document.getElementById('actualizado');
    function reloj() {
        const segundos = Math.max(0, Math.round((Date.now() - new Date(actualizado.dataset.calculado).getTime()) / 1000));
        actualizado.textContent = segundos < 10 ? 'actualizado ahora' : segundos < 60 ? `actualizado hace ${segundos} s` : `actualizado hace ${Math.floor(segundos / 60)} min`;
    }

    async function refrescar() {
        if (document.visibilityState !== 'visible') return;
        cargarVentas();
        try {
            const r = await fetch(raiz.dataset.url, { headers: { Accept: 'application/json' }, cache: 'no-store' });
            if (r.ok) pintar(await r.json());
        } catch (e) { /* sin conexión: se reintenta en el siguiente ciclo */ }
    }

    setInterval(refrescar, INTERVALO);
    setInterval(reloj, 5000);
    document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') refrescar(); });
    reloj();
    cargarVentas();
})();
