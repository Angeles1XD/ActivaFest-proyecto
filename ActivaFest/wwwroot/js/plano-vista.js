// =============================
// 🗺️ VISTA DEL PLANO: zoom y pestañas (plano interactivo / plano oficial del recinto)
// =============================
// En el celular el teatro se ve pequeño: con el zoom se agranda el plano y se recorre deslizando,
// como al elegir asiento en la web de un bus. El plano oficial es la imagen que subió el organizador.
(function () {
    'use strict';

    const panel = document.getElementById('panelPlanoInteractivo');
    if (!panel) return;

    // ---------- Zoom ----------
    const lienzo = panel.querySelector('.plano-lienzo');
    const svg = lienzo && lienzo.querySelector('svg');
    const niveles = [1, 1.5, 2, 2.75];
    let nivel = 0;

    function aplicar(nuevo) {
        if (!svg) return;
        // Se mantiene centrado lo que se estaba mirando
        const centroX = (lienzo.scrollLeft + lienzo.clientWidth / 2) / lienzo.scrollWidth;
        const centroY = (lienzo.scrollTop + lienzo.clientHeight / 2) / lienzo.scrollHeight;
        nivel = Math.max(0, Math.min(niveles.length - 1, nuevo));
        svg.style.width = (niveles[nivel] * 100) + '%';
        lienzo.classList.toggle('con-zoom', nivel > 0);
        lienzo.scrollLeft = centroX * lienzo.scrollWidth - lienzo.clientWidth / 2;
        lienzo.scrollTop = centroY * lienzo.scrollHeight - lienzo.clientHeight / 2;
        panel.querySelector('[data-zoom="-1"]').disabled = nivel === 0;
        panel.querySelector('[data-zoom="1"]').disabled = nivel === niveles.length - 1;
    }

    panel.querySelectorAll('[data-zoom]').forEach(function (boton) {
        boton.addEventListener('click', function () {
            const paso = parseInt(boton.dataset.zoom, 10);
            aplicar(paso === 0 ? 0 : nivel + paso);
        });
    });
    aplicar(0);

    // ---------- Pestañas ----------
    const pestanas = Array.from(document.querySelectorAll('.plano-pestanas [role="tab"]'));
    pestanas.forEach(function (pestana, i) {
        pestana.addEventListener('click', function () { mostrar(pestana); });
        pestana.addEventListener('keydown', function (e) {
            if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
            const otra = pestanas[(i + (e.key === 'ArrowRight' ? 1 : pestanas.length - 1)) % pestanas.length];
            otra.focus();
            mostrar(otra);
        });
    });

    function mostrar(elegida) {
        pestanas.forEach(function (p) {
            const activa = p === elegida;
            p.classList.toggle('activa', activa);
            p.setAttribute('aria-selected', activa ? 'true' : 'false');
            p.tabIndex = activa ? 0 : -1;
            document.getElementById(p.getAttribute('aria-controls')).hidden = !activa;
        });
    }
})();
