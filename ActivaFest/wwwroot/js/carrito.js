// =============================
// 🛒 CUENTA REGRESIVA DE LAS RESERVAS DEL CARRITO
// =============================
// Al vencer una reserva la página se recarga: el servidor la pasa a "Reservas vencidas" y actualiza el total.
(function () {
    'use strict';

    const relojes = document.querySelectorAll('.carrito-reloj');
    if (!relojes.length) return;

    function tick() {
        let vencio = false;
        relojes.forEach(function (reloj) {
            const restante = Math.max(0, Math.floor((new Date(reloj.dataset.vence).getTime() - Date.now()) / 1000));
            reloj.textContent = '⏳ ' + String(Math.floor(restante / 60)).padStart(2, '0') + ':' + String(restante % 60).padStart(2, '0');
            reloj.classList.toggle('urgente', restante < 120);
            if (restante === 0) vencio = true;
        });
        if (vencio) {
            clearInterval(intervalo);
            location.reload();
        }
    }

    const intervalo = setInterval(tick, 1000);
    tick();
})();
