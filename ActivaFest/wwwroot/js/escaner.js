// =============================
// 🚪 ESCÁNER DE ENTRADAS EN PUERTA (HU08)
// Lee el QR con la cámara (html5-qrcode), lo valida en el servidor y muestra el resultado a pantalla completa.
// =============================
(function () {
    'use strict';

    const raiz = document.getElementById('escaner');
    if (!raiz) return;

    const eventoId = parseInt(raiz.dataset.evento, 10);
    const token = document.querySelector('input[name="__RequestVerificationToken"]').value;

    const panel = document.getElementById('resultado');
    const icono = document.getElementById('resultadoIcono');
    const titulo = document.getElementById('resultadoTitulo');
    const mensaje = document.getElementById('resultadoMensaje');
    const detalle = document.getElementById('resultadoDetalle');
    const estadoCamara = document.getElementById('estadoCamara');

    // Cada resultado: clase de color + icono + texto (nunca solo color)
    const ESTILOS = {
        Valida: { clase: 'ok', icono: '✓' },
        YaUsada: { clase: 'error', icono: '✕' },
        Invalida: { clase: 'error', icono: '✕' },
        OtroEvento: { clase: 'error', icono: '✕' },
        Anulada: { clase: 'error', icono: '✕' },
        Temprano: { clase: 'error', icono: '⏳' },
        Vencida: { clase: 'error', icono: '✕' }
    };

    let lector = null;
    let ocupado = false;

    // ---------- Sonido y vibración ----------
    let audio = null;
    function pitido(ok) {
        try {
            audio = audio || new (window.AudioContext || window.webkitAudioContext)();
            const osc = audio.createOscillator();
            const gan = audio.createGain();
            osc.frequency.value = ok ? 880 : 220;
            osc.connect(gan);
            gan.connect(audio.destination);
            gan.gain.setValueAtTime(0.2, audio.currentTime);
            osc.start();
            osc.stop(audio.currentTime + (ok ? 0.15 : 0.5));
        } catch (e) { /* sin audio: no es crítico */ }

        if (navigator.vibrate) navigator.vibrate(ok ? 120 : [200, 100, 200]);
    }

    // ---------- Validación en el servidor ----------
    async function validar(contenido) {
        if (ocupado) return;
        ocupado = true;
        if (lector) { try { lector.pause(true); } catch (e) { } }

        try {
            const respuesta = await fetch('/api/acceso/validar', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
                body: JSON.stringify({ eventoId: eventoId, contenido: contenido })
            });

            if (respuesta.status === 401 || respuesta.status === 403) {
                mostrar({ tipo: 'Invalida', titulo: 'SIN PERMISO', mensaje: 'Tu sesión expiró o no tienes asignado este evento.' });
                return;
            }

            const datos = await respuesta.json();
            mostrar(datos);
            document.getElementById('ingresados').textContent = datos.ingresados;
            document.getElementById('vendidas').textContent = datos.vendidas;
        } catch (e) {
            console.error(e);
            mostrar({ tipo: 'Invalida', titulo: 'SIN CONEXIÓN', mensaje: 'No se pudo validar: vuelve a escanear.' });
        }
    }

    function mostrar(datos) {
        const estilo = ESTILOS[datos.tipo] || ESTILOS.Invalida;
        panel.className = 'resultado ' + estilo.clase;
        icono.textContent = estilo.icono;
        // HU08: «INGRESO PERMITIDO», «ENTRADA YA UTILIZADA» o «INGRESO RECHAZADO»
        titulo.textContent = datos.titulo || (datos.permiteIngreso ? 'INGRESO PERMITIDO' : 'INGRESO RECHAZADO');
        mensaje.textContent = datos.mensaje;

        const partes = [];
        // HU27: zona, asiento y puerta del titular
        if (datos.zona) partes.push('Zona: ' + datos.zona);
        if (datos.asiento) partes.push('Asiento: ' + datos.asiento);
        if (datos.puerta) partes.push(datos.puerta);
        if (datos.titular) partes.push('Titular: ' + datos.titular);
        if (datos.tipo === 'YaUsada' && datos.fechaUso) partes.push('Usada el ' + datos.fechaUso);
        detalle.textContent = partes.join(' · ');

        pitido(datos.permiteIngreso === true);
        document.getElementById('btnSiguiente').focus();
    }

    function siguiente() {
        panel.className = 'resultado oculto';
        ocupado = false;
        if (lector) { try { lector.resume(); } catch (e) { } }
    }

    document.getElementById('btnSiguiente').addEventListener('click', siguiente);

    // ---------- Ingreso manual (respaldo) ----------
    document.getElementById('formManual').addEventListener('submit', function (e) {
        e.preventDefault();
        const campo = document.getElementById('codigoManual');
        if (campo.value.trim()) {
            ocupado = false;
            validar(campo.value.trim());
            campo.value = '';
        }
    });

    // ---------- Cámara ----------
    if (typeof Html5Qrcode === 'undefined') {
        estadoCamara.textContent = 'No se pudo cargar el lector de QR. Usa el ingreso manual.';
        return;
    }

    lector = new Html5Qrcode('lector', { formatsToSupport: [Html5QrcodeSupportedFormats.QR_CODE] });
    lector.start(
        { facingMode: 'environment' },
        { fps: 10, qrbox: function (w, h) { const lado = Math.floor(Math.min(w, h) * 0.7); return { width: lado, height: lado }; } },
        function (texto) { validar(texto); },
        function () { /* fotogramas sin QR: se ignoran */ }
    ).then(function () {
        estadoCamara.textContent = 'Apunta la cámara al código QR de la entrada.';
    }).catch(function (err) {
        console.error(err);
        estadoCamara.textContent = 'No pudimos acceder a la cámara. Revisa el permiso del navegador (necesita HTTPS) o usa el ingreso manual.';
    });
})();
