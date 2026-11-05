// =============================
// 📍 "USAR MI UBICACIÓN" PARA RECOMENDACIONES (HU17)
// =============================
// Recarga la página con ?lat=&lng= (redondeados a ~10 m); la ubicación no se guarda en el servidor.
(function () {
    'use strict';

    const boton = document.getElementById('usarUbicacion');
    const aviso = document.getElementById('avisoUbicacion');
    if (!boton) return;

    function avisar(texto) {
        if (!aviso) return;
        aviso.textContent = texto;
        aviso.hidden = false;
    }

    boton.addEventListener('click', function () {
        if (!navigator.geolocation) return avisar('Tu navegador no permite compartir la ubicación.');
        boton.disabled = true;
        navigator.geolocation.getCurrentPosition(function (p) {
            const url = new URL(window.location.href);
            url.searchParams.set('lat', p.coords.latitude.toFixed(4));
            url.searchParams.set('lng', p.coords.longitude.toFixed(4));
            url.hash = boton.dataset.ancla || '';
            window.location.href = url.toString();
        }, function () {
            boton.disabled = false;
            avisar('No pudimos obtener tu ubicación. Revisa los permisos del navegador.');
        }, { timeout: 8000, maximumAge: 600000 });
    });
})();
