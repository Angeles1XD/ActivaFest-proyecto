// =============================
// 👥 Selección de clientes para una promoción (HU29)
// =============================
(function () {
    const formulario = document.getElementById("seleccionPromocion");
    if (!formulario) return;

    const checks = Array.from(formulario.querySelectorAll(".check-cliente"));
    const todos = document.getElementById("seleccionarTodos");
    const boton = document.getElementById("crearPromocion");
    const contador = document.getElementById("contadorSeleccion");

    function actualizar() {
        const marcados = checks.filter(c => c.checked).length;
        contador.textContent = marcados;
        boton.disabled = marcados === 0;
        todos.checked = checks.length > 0 && marcados === checks.length;
        todos.indeterminate = marcados > 0 && marcados < checks.length;
    }

    todos.disabled = checks.length === 0;
    todos.addEventListener("change", () => {
        checks.forEach(c => (c.checked = todos.checked));
        actualizar();
    });
    checks.forEach(c => c.addEventListener("change", actualizar));
    actualizar();
})();
