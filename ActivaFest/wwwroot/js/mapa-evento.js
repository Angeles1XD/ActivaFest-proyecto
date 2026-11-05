// =============================
// 🗺️ MAPA DEL EVENTO Y TRAZADO DE RUTA (HU04)
// Leaflet + OpenStreetMap para el mapa, OSRM para calcular la ruta desde la ubicación del usuario.
// =============================
(function () {
    'use strict';

    const contenedor = document.getElementById('mapaEvento');
    if (!contenedor) return;

    const info = document.getElementById('rutaInfo');
    const boton = document.getElementById('btnRuta');

    // Sin la librería del mapa (CDN caído o bloqueado) la página sigue funcionando con Google Maps
    if (typeof L === 'undefined') {
        contenedor.classList.add('mapa-no-disponible');
        contenedor.textContent = '📍 ' + contenedor.dataset.direccion + ' — el mapa no está disponible en este momento. Usa "Abrir en Google Maps".';
        boton.hidden = true;
        return;
    }

    const OSRM_URL = 'https://router.project-osrm.org/route/v1/driving/';
    const TIEMPO_MAXIMO_RUTA_MS = 10000;
    const destino = L.latLng(parseFloat(contenedor.dataset.lat), parseFloat(contenedor.dataset.lng));

    const mapa = L.map(contenedor, { scrollWheelZoom: false }).setView(destino, 16);

    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
    }).addTo(mapa);

    // Popup construido con nodos de texto para no interpretar HTML del usuario
    const popup = document.createElement('div');
    const titulo = document.createElement('strong');
    titulo.textContent = contenedor.dataset.titulo;
    popup.append(titulo, document.createElement('br'), contenedor.dataset.direccion);
    const marcadorEvento = L.marker(destino).addTo(mapa).bindPopup(popup).openPopup();

    // Vuelve a mostrar solo el recinto (sin ruta ni marcador del usuario)
    function soloMarcador() {
        if (capaRuta) { mapa.removeLayer(capaRuta); capaRuta = null; }
        if (marcadorUsuario) { mapa.removeLayer(marcadorUsuario); marcadorUsuario = null; }
        mapa.setView(destino, 16);
        marcadorEvento.openPopup();
    }

    let capaRuta = null;
    let marcadorUsuario = null;

    function mostrar(mensaje, esError) {
        info.textContent = mensaje;
        info.classList.toggle('error', !!esError);
    }

    function formatearDistancia(metros) {
        return metros >= 1000 ? (metros / 1000).toFixed(1) + ' km' : Math.round(metros) + ' m';
    }

    function formatearDuracion(segundos) {
        const minutos = Math.round(segundos / 60);
        if (minutos < 60) return minutos + ' min';
        return Math.floor(minutos / 60) + ' h ' + (minutos % 60) + ' min';
    }

    async function trazarRuta(origen) {
        const coords = `${origen.lng},${origen.lat};${destino.lng},${destino.lat}`;
        // Si el servicio de rutas no responde, no se deja al usuario esperando indefinidamente
        const limite = new AbortController();
        const temporizador = setTimeout(() => limite.abort(), TIEMPO_MAXIMO_RUTA_MS);
        let respuesta;
        try {
            respuesta = await fetch(`${OSRM_URL}${coords}?overview=full&geometries=geojson`, { signal: limite.signal });
        } finally {
            clearTimeout(temporizador);
        }
        if (!respuesta.ok) throw new Error('OSRM ' + respuesta.status);

        const datos = await respuesta.json();
        if (datos.code !== 'Ok' || !datos.routes.length) throw new Error('Sin ruta');

        const ruta = datos.routes[0];

        if (capaRuta) mapa.removeLayer(capaRuta);
        if (marcadorUsuario) mapa.removeLayer(marcadorUsuario);

        capaRuta = L.geoJSON(ruta.geometry, { style: { color: '#ff006e', weight: 5, opacity: 0.85 } }).addTo(mapa);
        marcadorUsuario = L.circleMarker(origen, {
            radius: 8, color: '#ffffff', weight: 2, fillColor: '#3388ff', fillOpacity: 1
        }).addTo(mapa).bindTooltip('Tu ubicación');

        mapa.fitBounds(capaRuta.getBounds(), { padding: [30, 30] });
        mostrar(`🚗 ${formatearDistancia(ruta.distance)} · aprox. ${formatearDuracion(ruta.duration)} en auto`);
    }

    boton.addEventListener('click', function () {
        if (!navigator.geolocation) {
            mostrar('Tu navegador no permite obtener la ubicación. Usa "Abrir en Google Maps".', true);
            return;
        }

        boton.disabled = true;
        mostrar('Obteniendo tu ubicación...');

        navigator.geolocation.getCurrentPosition(
            async function (pos) {
                mostrar('Calculando la ruta...');
                try {
                    await trazarRuta(L.latLng(pos.coords.latitude, pos.coords.longitude));
                } catch (e) {
                    console.error(e);
                    soloMarcador();
                    mostrar('No se pudo calcular la ruta en este momento. Intenta con "Abrir en Google Maps".', true);
                } finally {
                    boton.disabled = false;
                }
            },
            // HU04: si el usuario niega (o no se obtiene) su ubicación se muestra solo el marcador, sin error
            function (err) {
                boton.disabled = false;
                soloMarcador();
                const mensajes = {
                    1: 'Te mostramos la ubicación del evento. Si quieres ver la ruta, permite el acceso a tu ubicación en el navegador.',
                    2: 'Te mostramos la ubicación del evento; no pudimos determinar la tuya para trazar la ruta.',
                    3: 'Te mostramos la ubicación del evento; tu ubicación tardó demasiado en obtenerse.'
                };
                mostrar(mensajes[err.code] || 'Te mostramos la ubicación del evento.', false);
            },
            { enableHighAccuracy: true, timeout: 15000, maximumAge: 60000 }
        );
    });
})();
