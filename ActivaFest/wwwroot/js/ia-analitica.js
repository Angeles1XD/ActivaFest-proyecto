// =============================
// 🤖 CONSULTAS A LA IA DESDE EL DASHBOARD (HU24)
// =============================
// La respuesta trae el texto, la tabla de cifras fuente, el período y un gráfico (ia-respuesta.js los pinta).
(function () {
    'use strict';

    const formulario = document.getElementById('iaFormulario');
    if (!formulario) return;

    const conversacion = document.getElementById('iaConversacion');
    const pregunta = document.getElementById('iaPregunta');
    const boton = formulario.querySelector('button[type="submit"]');
    const token = formulario.querySelector('input[name="__RequestVerificationToken"]').value;
    const elemento = IaRespuesta.elemento;

    function mostrarPregunta(texto) {
        conversacion.appendChild(elemento('div', 'ia-msg ia-msg-admin', texto));
    }

    function mostrarRespuesta(r) {
        const caja = elemento('div', 'ia-msg ia-msg-ia' + (r.fueraDeAlcance ? ' ia-fuera' : ''));
        caja.appendChild(elemento('p', 'ia-texto', r.texto));
        if (r.funcion) {
            caja.appendChild(elemento('p', 'ia-fuente',
                `Fuente: ${r.funcion} · Período: ${r.periodo}${r.redactadaPorIa ? ' · redactado con IA' : ''}`));
        }
        IaRespuesta.completar(caja, r);
        if (r.sugerencias && r.sugerencias.length) {
            const chips = elemento('div', 'ia-ejemplos');
            r.sugerencias.forEach(function (s) {
                const b = elemento('button', 'ia-ejemplo', s);
                b.type = 'button';
                chips.appendChild(b);
            });
            caja.appendChild(chips);
        }
        conversacion.appendChild(caja);
        caja.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }

    async function preguntar(texto) {
        texto = texto.trim();
        if (!texto) return;
        mostrarPregunta(texto);
        pregunta.value = '';
        boton.disabled = true;
        const espera = elemento('div', 'ia-msg ia-msg-ia texto-tenue', 'Consultando los datos…');
        conversacion.appendChild(espera);
        try {
            const r = await fetch(formulario.dataset.url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
                body: JSON.stringify({ pregunta: texto })
            });
            if (!r.ok) throw new Error(r.status);
            espera.remove();
            mostrarRespuesta(await r.json());
        } catch (e) {
            espera.textContent = 'No se pudo hacer la consulta. Inténtalo de nuevo.';
        } finally {
            boton.disabled = false;
            pregunta.focus();
        }
    }

    formulario.addEventListener('submit', function (e) {
        e.preventDefault();
        preguntar(pregunta.value);
    });

    // Los ejemplos (y las sugerencias de cada respuesta) se preguntan con un clic.
    // Los botones de los gráficos llevan su pregunta y usan el período del dashboard, para que coincidan las cifras.
    const analisis = document.getElementById('analisis');
    document.addEventListener('click', function (e) {
        const chip = e.target.closest('.ia-ejemplo');
        if (!chip) return;
        if (!chip.dataset.pregunta) {
            preguntar(chip.textContent);
            return;
        }
        const periodo = 'sinPeriodo' in chip.dataset || !analisis ? '' : ' ' + analisis.dataset.periodo;
        const evento = analisis && analisis.dataset.evento ? ' del evento ' + analisis.dataset.evento : '';
        document.querySelector('.panel-ia').scrollIntoView({ behavior: 'smooth', block: 'start' });
        preguntar(chip.dataset.pregunta.replace(/\?$/, '') + evento + periodo);
    });
})();
