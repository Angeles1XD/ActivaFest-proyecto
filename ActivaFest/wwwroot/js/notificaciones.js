// =============================
// 🔔 PANEL DE NOTIFICACIONES (campana de la barra superior)
// =============================
// Muestra los recordatorios y avisos que el usuario pidió al asistente. Todo el texto va con textContent.
(function () {
    const campana = document.getElementById('notifCampana');
    if (!campana) return;

    const boton = document.getElementById('notifBoton');
    const panel = document.getElementById('notifPanel');
    const contenido = document.getElementById('notifContenido');
    const contador = document.getElementById('notifContador');

    function el(tag, clase, texto) {
        const n = document.createElement(tag);
        if (clase) n.className = clase;
        if (texto != null) n.textContent = texto;
        return n;
    }

    function urlSegura(url) {
        return typeof url === 'string' && url.startsWith('/') && !url.startsWith('//');
    }

    function pintarContador(n) {
        contador.hidden = !n;
        contador.textContent = n > 9 ? '9+' : String(n || 0);
        boton.setAttribute('aria-label', n ? 'Notificaciones (' + n + ' sin leer)' : 'Notificaciones');
    }

    function item(n) {
        const fila = el(urlSegura(n.url) ? 'a' : 'div', 'notif-item' + (n.leida ? '' : ' notif-nueva'));
        if (urlSegura(n.url)) fila.href = n.url;
        fila.appendChild(el('span', 'notif-icono', n.icono));
        const cuerpo = el('div', 'notif-cuerpo');
        cuerpo.appendChild(el('strong', null, n.titulo));
        if (n.mensaje) cuerpo.appendChild(el('span', null, n.mensaje));
        cuerpo.appendChild(el('span', 'notif-fecha', n.fecha || n.cuando || ''));
        fila.appendChild(cuerpo);
        return fila;
    }

    async function cargar() {
        try {
            const r = await fetch('/api/notificaciones', { headers: { Accept: 'application/json' } });
            if (!r.ok) throw new Error();
            const datos = await r.json();
            pintarContador(datos.noLeidas);

            contenido.replaceChildren();
            if (datos.programadas.length) {
                contenido.appendChild(el('div', 'notif-subtitulo', 'Programadas'));
                datos.programadas.forEach(function (p) {
                    contenido.appendChild(item({ icono: p.icono, titulo: p.texto, cuando: p.cuando ? 'Se enviará el ' + p.cuando : 'Activo', leida: true }));
                });
            }
            contenido.appendChild(el('div', 'notif-subtitulo', 'Recibidas'));
            if (!datos.notificaciones.length) {
                contenido.appendChild(el('p', 'notif-vacio', 'Aún no tienes notificaciones. Pídele al asistente 💬 "recuérdame" y el nombre de un evento.'));
            }
            datos.notificaciones.forEach(function (n) { contenido.appendChild(item(n)); });
            return datos;
        } catch (e) {
            contenido.textContent = 'No se pudieron cargar las notificaciones.';
            return null;
        }
    }

    async function actualizarContador() {
        try {
            const r = await fetch('/api/notificaciones/contador', { headers: { Accept: 'application/json' } });
            if (r.ok) pintarContador((await r.json()).noLeidas);
        } catch (e) { /* sin conexión: se reintenta en el siguiente ciclo */ }
    }

    async function abrir(abrirPanel) {
        panel.hidden = !abrirPanel;
        boton.setAttribute('aria-expanded', String(abrirPanel));
        if (!abrirPanel) return;
        const datos = await cargar();
        // Al verlas quedan leídas (el resaltado se mantiene hasta cerrar el panel)
        if (datos && datos.noLeidas) {
            fetch('/api/notificaciones/leidas', { method: 'POST' }).then(function () { pintarContador(0); });
        }
    }

    boton.addEventListener('click', function (e) {
        e.stopPropagation();
        abrir(panel.hidden);
    });
    document.addEventListener('click', function (e) {
        if (!panel.hidden && !campana.contains(e.target)) abrir(false);
    });
    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && !panel.hidden) { abrir(false); boton.focus(); }
    });

    // El chat avisa cuando el asistente crea un recordatorio (site.js)
    window.actualizarNotificaciones = function () {
        if (!panel.hidden) cargar(); else actualizarContador();
    };

    actualizarContador();
    setInterval(actualizarContador, 60000);
})();
