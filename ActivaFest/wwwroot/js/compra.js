// =============================
// 🛒 SELECCIÓN DE ENTRADAS Y CUENTA REGRESIVA DE LA RESERVA
// =============================
(function () {
    'use strict';

    const formato = new Intl.NumberFormat('es-PE', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

    // ---------- Total estimado en la ficha (el total real lo calcula el servidor) ----------
    const selectores = document.querySelectorAll('.selector-cantidad');
    const total = document.getElementById('totalCompra');
    const boton = document.getElementById('btnComprar');

    function recalcular() {
        let suma = 0;
        let cantidad = 0;
        selectores.forEach(function (s) {
            const n = parseInt(s.value, 10) || 0;
            cantidad += n;
            suma += n * parseFloat(s.dataset.precio);
        });

        if (total) total.textContent = suma === 0 && cantidad > 0 ? 'Gratis' : 'S/ ' + formato.format(suma);
        if (boton) {
            boton.disabled = cantidad === 0 || cantidad > 10;
            boton.textContent = cantidad > 10 ? 'Máximo 10 entradas por compra' : '🎟️ Comprar entradas';
        }
    }

    // "Agregar al carrito" se habilita junto con "Comprar" (también cuando lo controla el plano de asientos)
    const carrito = document.getElementById('btnCarrito');
    if (boton && carrito) {
        new MutationObserver(function () { carrito.disabled = boton.disabled; }).observe(boton, { attributes: true, attributeFilter: ['disabled'] });
    }

    selectores.forEach(function (s) { s.addEventListener('change', recalcular); });
    if (selectores.length) recalcular();

    // ---------- Cuenta regresiva en el checkout ----------
    const reloj = document.getElementById('relojReserva');
    if (reloj) {
        const vence = new Date(reloj.dataset.vence).getTime();
        const pagar = document.getElementById('btnPagar');

        const tick = function () {
            const restante = Math.max(0, Math.floor((vence - Date.now()) / 1000));
            const min = String(Math.floor(restante / 60)).padStart(2, '0');
            const seg = String(restante % 60).padStart(2, '0');
            reloj.textContent = min + ':' + seg;
            reloj.classList.toggle('urgente', restante < 120);

            if (restante === 0) {
                clearInterval(intervalo);
                if (pagar) {
                    pagar.disabled = true;
                    pagar.textContent = 'Tu reserva expiró';
                }
            }
        };

        const intervalo = setInterval(tick, 1000);
        tick();
    }
})();
