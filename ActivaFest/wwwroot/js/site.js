// =============================
// 🧭 MENÚ MÓVIL Y 🤖 ASISTENTE VIRTUAL (comunes a todas las páginas)
// =============================
(function () {
    'use strict';

    // ---------- Menú hamburguesa ----------
    const toggle = document.getElementById('navToggle');
    const menu = document.getElementById('navMenu');
    if (toggle && menu) {
        toggle.addEventListener('click', function () {
            const abierto = menu.classList.toggle('abierto');
            toggle.setAttribute('aria-expanded', String(abierto));
            toggle.setAttribute('aria-label', abierto ? 'Cerrar menú' : 'Abrir menú');
        });
    }

    // ---------- ⏱️ Tiempo restante de un destacado (HU12) ----------
    // El servidor indica los segundos que quedan; aquí solo se descuentan (no depende del reloj del usuario)
    document.querySelectorAll('.temporizador-destacado').forEach(function (el) {
        const fin = Date.now() + (parseInt(el.dataset.segundos, 10) || 0) * 1000;
        const pintar = function () {
            const s = Math.max(0, Math.floor((fin - Date.now()) / 1000));
            const d = Math.floor(s / 86400), h = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60);
            el.textContent = s === 0 ? 'periodo terminado'
                : '⏱️ quedan ' + (d > 0 ? d + ' d ' : '') + (d > 0 || h > 0 ? h + ' h ' : '') + m + ' min' + (d === 0 && h === 0 ? ' ' + (s % 60) + ' s' : '');
            if (s === 0) clearInterval(intervalo);
        };
        const intervalo = setInterval(pintar, 1000);
        pintar();
    });

    // ---------- Chat del asistente (HU14 / RF12) ----------
    const ventana = document.getElementById('chat-window');
    const mensajes = document.getElementById('chat-messages');
    const entrada = document.getElementById('chat-input');
    const escribiendo = document.getElementById('chat-typing');
    if (!ventana) return;

    // Bienvenida según el rol de quien abre el chat (data-rol en _Layout)
    const widget = document.getElementById('chat-widget');
    const BIENVENIDAS = {
        admin: {
            texto: '¡Hola! Soy el asistente de ActivaFest 👋 Como administrador puedes consultarme eventos, ventas, comisiones y compradores de toda la página, pedirme el gráfico que necesites y recomendaciones.',
            sugerencias: ['Gráfico de pastel de ingresos por categoría', 'Dime los eventos que ya he aprobado', '¿Qué eventos faltan aprobar?']
        },
        acceso: {
            texto: '¡Hola! Soy el asistente de ActivaFest 👋 Puedo recordarte tus turnos de control de acceso en tus notificaciones 🔔, reservarte entradas y ayudarte a encontrar planes.',
            sugerencias: ['Recuérdame mis turnos', 'Mis avisos', '¿Qué eventos hay este fin de semana?']
        },
        organizador: {
            texto: '¡Hola! Soy el asistente de ActivaFest 👋 Puedo mostrarte tu dashboard de ventas, hacerte gráficos de los eventos que publicaste y decirte cuáles siguen pendientes de aprobación.',
            sugerencias: ['Muéstrame mi dashboard de ventas', 'Gráfico de barras de mis eventos más vendidos', '¿Qué eventos tengo pendientes de aprobación?']
        },
        usuario: {
            texto: '¡Hola! Soy tu asistente de ActivaFest 👋 Dime qué evento quieres y te ayudo a reservar o comprar tus entradas: te muestro el resumen y solo lo hago cuando me confirmes. También te ayudo con tus compras y a encontrar planes.',
            sugerencias: ['Quiero reservar una entrada', 'Quiero comprar una entrada', 'Ver mi carrito']
        },
        visitante: {
            texto: '¡Hola! Soy el asistente de ActivaFest 👋 Pregúntame por eventos, horarios, precios, ubicación o cómo comprar.',
            sugerencias: ['¿Qué eventos hay este fin de semana?', 'Recomiéndame algo', '¿Cómo compro entradas?']
        }
    };
    const BIENVENIDA = BIENVENIDAS[widget && widget.dataset.rol] || BIENVENIDAS.visitante;

    // En la ficha de un evento, el asistente sabe de qué evento se habla ("¿dónde es?")
    const contexto = document.getElementById('chat-contexto');
    const eventoDeLaPagina = contexto ? parseInt(contexto.dataset.evento, 10) || null : null;
    let eventoId = eventoDeLaPagina;
    let ubicacion = null;

    // Reserva o compra a medio conversar: el servidor la entrega y se le devuelve con el siguiente mensaje
    let pendiente = null;

    // La conversación vive solo en memoria: cada vez que se abre el chat empieza de cero y nunca queda guardada
    // en el navegador, donde la vería la siguiente persona que use el mismo equipo
    let conversacion = [];
    try { localStorage.removeItem('activafest_chat_v2'); } catch (e) { /* sin almacenamiento */ }

    // Solo enlaces internos o a Google Maps: una respuesta nunca puede llevar a un sitio arbitrario
    function urlSegura(url) {
        return typeof url === 'string' &&
            ((url.startsWith('/') && !url.startsWith('//')) || url.startsWith('https://www.google.com/maps/'));
    }

    function quitarSugerencias() {
        mensajes.querySelectorAll('.chat-sugerencias').forEach(function (s) { s.remove(); });
    }

    // HU28: plano del recinto con la ubicación del usuario resaltada (SVG de su propia entrada, mismo origen).
    // Se interpreta como SVG (no como HTML) y se quitan scripts y atributos de eventos por si acaso.
    function planoEnChat(url) {
        const caja = document.createElement('a');
        caja.className = 'chat-plano';
        caja.href = url;
        caja.target = '_blank';
        caja.rel = 'noopener';
        caja.title = 'Abrir el plano en grande';
        caja.textContent = 'Cargando el plano…';
        fetch(url, { headers: { Accept: 'image/svg+xml' } })
            .then(function (r) { if (!r.ok) throw new Error(); return r.text(); })
            .then(function (texto) {
                const doc = new DOMParser().parseFromString(texto, 'image/svg+xml');
                const svg = doc.documentElement;
                if (svg.nodeName !== 'svg' || doc.querySelector('parsererror')) throw new Error();
                svg.querySelectorAll('script, foreignObject').forEach(function (n) { n.remove(); });
                svg.querySelectorAll('*').forEach(function (n) {
                    Array.from(n.attributes).forEach(function (a) { if (/^on/i.test(a.name)) n.removeAttribute(a.name); });
                });
                caja.replaceChildren(document.importNode(svg, true));
                mensajes.scrollTop = mensajes.scrollHeight;
            })
            .catch(function () { caja.textContent = 'Ver el plano con tu ubicación'; });
        return caja;
    }

    // Dashboard de ventas del organizador: indicadores, barras de ocupación y ventas por día (todo con textContent)
    function dashboardEnChat(d) {
        function el(tag, clase, texto) {
            const n = document.createElement(tag);
            if (clase) n.className = clase;
            if (texto != null) n.textContent = texto;
            return n;
        }
        const caja = el('div', 'chat-dashboard');
        if (d.titulo) caja.appendChild(el('div', 'chat-dash-titulo', d.titulo));

        if (d.indicadores && d.indicadores.length) {
            const grilla = el('div', 'chat-dash-kpis');
            d.indicadores.forEach(function (k) {
                const kpi = el('div', 'chat-dash-kpi');
                kpi.appendChild(el('span', 'chat-dash-valor', k.valor));
                kpi.appendChild(el('span', 'chat-dash-etiqueta', k.etiqueta));
                grilla.appendChild(kpi);
            });
            caja.appendChild(grilla);
        }

        if (d.barras && d.barras.length) {
            if (d.tituloBarras) caja.appendChild(el('div', 'chat-dash-subtitulo', d.tituloBarras));
            d.barras.forEach(function (b) {
                const fila = el('div', 'chat-dash-barra');
                const cabecera = el('div', 'chat-dash-barra-cab');
                cabecera.appendChild(el('span', 'chat-dash-barra-nombre', b.etiqueta));
                cabecera.appendChild(el('span', null, Math.round((b.porcentaje || 0) * 100) + ' %'));
                fila.appendChild(cabecera);
                const pista = el('div', 'chat-dash-pista');
                const relleno = el('div', 'chat-dash-relleno');
                relleno.style.width = Math.min(100, Math.max(0, (b.porcentaje || 0) * 100)) + '%';
                pista.appendChild(relleno);
                fila.appendChild(pista);
                if (b.detalle) fila.appendChild(el('div', 'chat-dash-detalle', b.detalle));
                caja.appendChild(fila);
            });
        }

        if (d.serie && d.serie.length) {
            if (d.tituloSerie) caja.appendChild(el('div', 'chat-dash-subtitulo', d.tituloSerie));
            const maximo = Math.max.apply(null, d.serie.map(function (p) { return p.valor; }).concat([1]));
            const columnas = el('div', 'chat-dash-serie');
            d.serie.forEach(function (p) {
                const col = el('div', 'chat-dash-col');
                col.title = p.etiqueta + ': ' + p.valor;
                col.appendChild(el('span', 'chat-dash-col-valor', String(p.valor)));
                const barra = el('div', 'chat-dash-col-barra');
                barra.style.height = Math.max(4, Math.round(56 * p.valor / maximo)) + 'px';
                col.appendChild(barra);
                col.appendChild(el('span', 'chat-dash-col-dia', p.etiqueta));
                columnas.appendChild(col);
            });
            caja.appendChild(columnas);
        }
        return caja;
    }

    // textContent (no innerHTML): ni el usuario ni el asistente pueden inyectar HTML
    function mostrar(m) {
        const div = document.createElement('div');
        div.className = 'msg msg-' + m.autor;
        div.textContent = m.texto;
        if (m.dashboard) {
            div.classList.add('msg-con-dashboard');
            div.appendChild(dashboardEnChat(m.dashboard));
        }
        if (m.tabla || m.grafico || (m.recomendaciones && m.recomendaciones.length)) {
            div.classList.add('msg-con-dashboard');
            IaRespuesta.completar(div, m);
        }

        const enlaces = (m.enlaces || []).filter(function (e) { return urlSegura(e.url); });
        if (enlaces.length) {
            const lista = document.createElement('div');
            lista.className = 'chat-enlaces';
            enlaces.forEach(function (e) {
                const a = document.createElement('a');
                a.href = e.url;
                a.textContent = e.texto;
                if (e.url.startsWith('https://')) { a.target = '_blank'; a.rel = 'noopener noreferrer'; }
                lista.appendChild(a);
            });
            div.appendChild(lista);
        }
        if (m.plano && urlSegura(m.plano)) div.appendChild(planoEnChat(m.plano));
        mensajes.appendChild(div);

        if (m.sugerencias && m.sugerencias.length) {
            quitarSugerencias();
            const chips = document.createElement('div');
            chips.className = 'chat-sugerencias';
            m.sugerencias.forEach(function (s) {
                const b = document.createElement('button');
                b.type = 'button';
                b.textContent = s;
                b.addEventListener('click', function () { preguntar(s); });
                chips.appendChild(b);
            });
            mensajes.appendChild(chips);
        }
        mensajes.scrollTop = mensajes.scrollHeight;
    }

    function alternar() {
        const abrir = !ventana.classList.contains('abierto');
        ventana.classList.toggle('abierto', abrir);
        if (abrir) {
            reiniciar();
            entrada.focus();
        } else if (window.speechSynthesis) {
            speechSynthesis.cancel();
        }
    }

    function reiniciar() {
        conversacion = [];
        eventoId = eventoDeLaPagina;
        pendiente = null;
        mensajes.replaceChildren();
        mostrar({ autor: 'bot', texto: BIENVENIDA.texto, sugerencias: BIENVENIDA.sugerencias });
    }

    // La ubicación solo se pide si el usuario pregunta por recomendaciones o cercanía, y no se guarda
    function pedirUbicacion(texto) {
        if (ubicacion || !navigator.geolocation || !/recom|sugi|cerca|plan/i.test(texto)) return Promise.resolve();
        return new Promise(function (listo) {
            navigator.geolocation.getCurrentPosition(
                function (p) { ubicacion = { latitud: p.coords.latitude, longitud: p.coords.longitude }; listo(); },
                function () { listo(); },
                { timeout: 5000, maximumAge: 600000 });
        });
    }

    async function preguntar(texto) {
        texto = texto.trim();
        if (!texto) return;

        quitarSugerencias();
        // Contexto para la IA: últimos mensajes antes de esta pregunta y si se dictó por voz (asistente-voz.js)
        const previosIa = conversacion.slice(-6);
        const modo = entrada.dataset.modo === 'voz' ? 'voz' : 'chat';
        delete entrada.dataset.modo;
        const mensajeUsuario = { autor: 'user', texto: texto };
        mostrar(mensajeUsuario);
        conversacion.push(mensajeUsuario);
        entrada.value = '';
        escribiendo.style.display = 'block';

        try {
            await pedirUbicacion(texto);
            const r = await fetch('/api/asistente-ia', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    mensaje: texto,
                    historial: previosIa,
                    modo: modo,
                    eventoId: eventoId,
                    pendiente: pendiente,
                    latitud: ubicacion ? ubicacion.latitud : null,
                    longitud: ubicacion ? ubicacion.longitud : null
                })
            });
            if (r.status === 429) throw new Error('limite');
            if (!r.ok) throw new Error('servidor');

            const datos = await r.json();
            if (datos.eventoId) eventoId = datos.eventoId; // para preguntas de seguimiento
            pendiente = datos.pendiente || null;
            mostrar({
                autor: 'bot', texto: datos.reply, enlaces: datos.enlaces, sugerencias: datos.sugerencias, plano: datos.plano, dashboard: datos.dashboard,
                tabla: datos.tabla, grafico: datos.grafico, recomendaciones: datos.recomendaciones
            });
            conversacion.push({ autor: 'bot', texto: datos.reply });
            // El usuario confirmó la compra: unos segundos para leer el mensaje y pasa a la pasarela
            if (urlSegura(datos.redirigir)) setTimeout(function () { location.href = datos.redirigir; }, 1800);
            // Si el asistente programó un recordatorio, la campana de notificaciones se actualiza (notificaciones.js)
            if (typeof window.actualizarNotificaciones === 'function') window.actualizarNotificaciones();
        } catch (err) {
            mostrar({
                autor: 'bot',
                texto: err.message === 'limite'
                    ? 'Estás enviando muchos mensajes seguidos. Espera un minuto y vuelve a intentarlo.'
                    : 'No pude conectarme con el asistente en este momento. Inténtalo de nuevo en unos minutos.'
            });
        } finally {
            escribiendo.style.display = 'none';
        }
    }

    reiniciar();

    document.getElementById('chat-toggle').addEventListener('click', alternar);
    document.getElementById('chat-cerrar').addEventListener('click', alternar);
    document.getElementById('chat-input-area').addEventListener('submit', function (e) {
        e.preventDefault();
        preguntar(entrada.value);
    });
})();
