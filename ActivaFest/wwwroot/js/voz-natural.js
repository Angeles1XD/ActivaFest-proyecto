// =============================
// 🗣️ VOZ NATURAL DEL ASISTENTE
// =============================
// Prepara el texto antes de leerlo: los montos se dicen en soles ("S/ 120.50" -> "ciento veinte soles con cincuenta
// céntimos"), los emojis y viñetas se quitan, y las respuestas largas se parten por frases.
// También elige la voz de mejor calidad que tenga el navegador. En pantalla el texto no cambia.
window.VozNatural = (function () {
    'use strict';

    const LARGO_MAXIMO = 200;   // Chrome corta las voces en línea a los ~15 segundos de una misma locución

    const HASTA_29 = ['', 'uno', 'dos', 'tres', 'cuatro', 'cinco', 'seis', 'siete', 'ocho', 'nueve', 'diez',
        'once', 'doce', 'trece', 'catorce', 'quince', 'dieciséis', 'diecisiete', 'dieciocho', 'diecinueve', 'veinte',
        'veintiuno', 'veintidós', 'veintitrés', 'veinticuatro', 'veinticinco', 'veintiséis', 'veintisiete', 'veintiocho', 'veintinueve'];
    const DECENAS = ['', '', '', 'treinta', 'cuarenta', 'cincuenta', 'sesenta', 'setenta', 'ochenta', 'noventa'];
    const CENTENAS = ['', 'ciento', 'doscientos', 'trescientos', 'cuatrocientos', 'quinientos', 'seiscientos', 'setecientos', 'ochocientos', 'novecientos'];

    function hasta999(n) {
        if (n === 100) return 'cien';
        const resto = n % 100;
        const unidades = resto < 30 ? HASTA_29[resto] : DECENAS[Math.floor(resto / 10)] + (resto % 10 ? ' y ' + HASTA_29[resto % 10] : '');
        return [CENTENAS[Math.floor(n / 100)], unidades].filter(Boolean).join(' ');
    }

    // Delante de un sustantivo: "veintiún soles", "treinta y un mil"
    function apocopar(texto) {
        return texto.replace(/veintiuno$/, 'veintiún').replace(/uno$/, 'un');
    }

    function enLetras(n) {
        if (n === 0) return 'cero';
        const millones = Math.floor(n / 1000000);
        const miles = Math.floor(n % 1000000 / 1000);
        const partes = [];
        if (millones) partes.push(millones === 1 ? 'un millón' : apocopar(enLetras(millones)) + ' millones');
        if (miles) partes.push(miles === 1 ? 'mil' : apocopar(hasta999(miles)) + ' mil');
        if (n % 1000) partes.push(hasta999(n % 1000));
        return partes.join(' ');
    }

    function unidadMonetaria(n, singular, plural) {
        if (n === 1) return 'un ' + singular;
        return apocopar(enLetras(n)) + (n >= 1000000 && n % 1000000 === 0 ? ' de ' : ' ') + plural;
    }

    // Acepta "1,250.50", "1.250,50", "120.5" y "1,250": solo 1 o 2 cifras finales son céntimos
    function monto(numero) {
        const partes = numero.match(/^(.*?)(?:[.,](\d{1,2}))?$/);
        const soles = Number(partes[1].replace(/\D/g, ''));
        const centimos = Number((partes[2] || '').padEnd(2, '0'));
        if (soles === 0 && centimos > 0) return unidadMonetaria(centimos, 'céntimo', 'céntimos');
        const dicho = unidadMonetaria(soles, 'sol', 'soles');
        return centimos ? dicho + ' con ' + unidadMonetaria(centimos, 'céntimo', 'céntimos') : dicho;
    }

    function paraVoz(texto) {
        return texto
            .replace(/\bS\/\.?\s*(\d+(?:[.,]\d+)*)/gi, function (_, n) { return monto(n); })
            .replace(/(\d+(?:[.,]\d+)*)\s*soles\b/gi, function (_, n) { return monto(n); })
            .replace(/[\u{1F000}-\u{1FFFF}\u{2600}-\u{27BF}\u{FE0F}«»*_#]/gu, ' ')
            .replace(/\s*[\r\n•]+\s*/g, '. ')              // cada línea o viñeta es una frase: hace su pausa
            .replace(/\s*·\s*|\s+[–—]\s+/g, ', ')
            .replace(/([.,;:!?¿¡…])(?:\s*\.)+(?=\s|$)/g, '$1')
            .replace(/^[\s.,]+/, '')
            .replace(/\s+/g, ' ')
            .trim();
    }

    // Une frases hasta el largo máximo: menos cortes entre locuciones se oye más fluido.
    // Una frase demasiado larga se parte por sus comas.
    function trozos(texto) {
        const piezas = [];
        texto.replace(/([.!?…])\s+/g, '$1\n').split('\n').forEach(function (frase) {
            if (frase.length <= LARGO_MAXIMO) piezas.push(frase);
            else piezas.push.apply(piezas, frase.replace(/,\s+/g, ',\n').split('\n'));
        });

        const resultado = [];
        piezas.filter(Boolean).forEach(function (pieza) {
            const ultimo = resultado.length - 1;
            if (ultimo >= 0 && resultado[ultimo].length + pieza.length < LARGO_MAXIMO) resultado[ultimo] += ' ' + pieza;
            else resultado.push(pieza);
        });
        return resultado;
    }

    // Primero las voces neuronales (Edge: "Natural", Safari: "Premium"/"Enhanced", Chrome: "Google"),
    // y a igual calidad la de acento más cercano: Perú, luego Latinoamérica, luego España.
    function puntaje(voz) {
        const calidad = /natural|neural|premium|enhanced|mejorada/i.test(voz.name) ? 40
            : /google/i.test(voz.name) ? 30
            : /online/i.test(voz.name) || voz.localService === false ? 20 : 0;
        const acento = /^es[-_]PE/i.test(voz.lang) ? 10 : /^es[-_]ES/i.test(voz.lang) ? 0 : 5;
        return calidad + acento;
    }

    function elegirVoz(voces) {
        return Array.from(voces)
            .filter(function (v) { return /^es/i.test(v.lang); })
            .sort(function (a, b) { return puntaje(b) - puntaje(a); })[0] || null;
    }

    return { monto: monto, paraVoz: paraVoz, trozos: trozos, elegirVoz: elegirVoz };
})();
