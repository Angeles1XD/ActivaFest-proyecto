// =============================
// 🎤 ASISTENTE VIRTUAL POR VOZ
// =============================
// Dictado con la Web Speech API del navegador (Chrome, Edge, Safari): el texto reconocido se envía por el mismo
// formulario del chat (site.js) marcado como "voz", y la respuesta del asistente se lee en voz alta
// (voz-natural.js prepara el texto y elige la voz).
// En navegadores sin reconocimiento de voz el botón del micrófono no aparece y el chat funciona igual.
(function () {
    'use strict';

    const Reconocimiento = window.SpeechRecognition || window.webkitSpeechRecognition;
    const boton = document.getElementById('chat-voz');
    const entrada = document.getElementById('chat-input');
    const formulario = document.getElementById('chat-input-area');
    const mensajes = document.getElementById('chat-messages');
    if (!Reconocimiento || !boton || !entrada || !formulario || !mensajes) return;

    const placeholder = entrada.placeholder;
    let reconocedor = null;
    let escuchando = false;
    let leerSiguiente = false;

    boton.hidden = false;
    if (window.speechSynthesis) speechSynthesis.getVoices();   // el navegador carga la lista de voces en segundo plano

    function estado(activo, texto) {
        escuchando = activo;
        boton.classList.toggle('escuchando', activo);
        boton.setAttribute('aria-pressed', String(activo));
        boton.setAttribute('aria-label', activo ? 'Detener dictado' : 'Preguntar por voz');
        entrada.placeholder = texto || placeholder;
    }

    function hablar(texto) {
        if (!window.speechSynthesis || !texto) return;
        const trozos = VozNatural.trozos(VozNatural.paraVoz(texto));
        if (!trozos.length) return;
        speechSynthesis.cancel();
        const voz = VozNatural.elegirVoz(speechSynthesis.getVoices());
        trozos.forEach(function (trozo) {
            const locucion = new SpeechSynthesisUtterance(trozo);
            locucion.lang = voz ? voz.lang : 'es-PE';
            if (voz) locucion.voice = voz;
            speechSynthesis.speak(locucion);
        });
    }

    function escuchar() {
        if (window.speechSynthesis) speechSynthesis.cancel();
        reconocedor = new Reconocimiento();
        reconocedor.lang = 'es-PE';
        reconocedor.interimResults = true;
        reconocedor.maxAlternatives = 1;

        let final = '';
        reconocedor.onresult = function (e) {
            let parcial = '';
            for (let i = e.resultIndex; i < e.results.length; i++) {
                if (e.results[i].isFinal) final += e.results[i][0].transcript;
                else parcial += e.results[i][0].transcript;
            }
            entrada.value = (final + parcial).trim();
        };
        reconocedor.onerror = function (e) {
            estado(false, e.error === 'not-allowed' || e.error === 'service-not-allowed'
                ? 'Permite el micrófono para hablar con el asistente'
                : e.error === 'no-speech' ? 'No te escuché, inténtalo de nuevo' : placeholder);
        };
        reconocedor.onend = function () {
            const texto = final.trim();
            if (escuchando) estado(false);
            if (!texto) return;
            entrada.value = texto;
            entrada.dataset.modo = 'voz';
            leerSiguiente = true;
            formulario.requestSubmit();
        };

        estado(true, 'Escuchando… habla ahora');
        try {
            reconocedor.start();
        } catch (err) {
            estado(false);
        }
    }

    boton.addEventListener('click', function () {
        if (escuchando && reconocedor) {
            reconocedor.stop();
            return;
        }
        escuchar();
    });

    // Lee en voz alta la respuesta a una pregunta dictada (solo el texto, no los enlaces)
    new MutationObserver(function (cambios) {
        if (!leerSiguiente) return;
        cambios.forEach(function (c) {
            c.addedNodes.forEach(function (n) {
                if (!leerSiguiente || !(n instanceof HTMLElement) || !n.classList.contains('msg-bot')) return;
                leerSiguiente = false;
                const texto = n.firstChild && n.firstChild.nodeType === Node.TEXT_NODE ? n.firstChild.nodeValue : n.textContent;
                hablar(texto);
            });
        });
    }).observe(mensajes, { childList: true });

    // Si el usuario escribe a mano, deja de leer en voz alta
    entrada.addEventListener('keydown', function () {
        if (window.speechSynthesis && speechSynthesis.speaking) speechSynthesis.cancel();
    });
})();
