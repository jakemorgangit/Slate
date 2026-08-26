// Interop helpers for Slate.
//
// Dragging is done with pointer events rather than HTML5 drag-and-drop: WebView2's
// composition hosting fires dragstart and then cancels immediately, so dragover and drop
// never arrive. Pointer events work normally, so every gesture here is built on them.

window.planner = (() => {
    let resize = null;
    let drag = null;
    let shortcutRef = null;

    const DRAG_THRESHOLD_PX = 4;
    const EDGE_SCROLL_ZONE_PX = 48;
    const EDGE_SCROLL_SPEED_PX = 14;

    // ---------------------------------------------------------------- theme

    function applyTheme(theme, accent, compact) {
        const root = document.documentElement;
        const resolved = theme === 'System' ? systemTheme() : theme.toLowerCase();
        root.setAttribute('data-theme', resolved);
        root.setAttribute('data-accent', (accent || 'violet').toLowerCase());
        root.setAttribute('data-density', compact ? 'compact' : 'cozy');
    }

    function systemTheme() {
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
    }

    // ---------------------------------------------------------------- dragging

    /**
     * Arms a drag. Nothing visible happens until the pointer moves past the threshold, so a
     * plain click on the same element still behaves as a click.
     *
     * kind is 'workitem' or 'allocation'; id is the work item id or the allocation guid.
     */
    function beginDrag(dotNetRef, kind, id, label, minutes, clientX, clientY, slotPixels, slotMinutes,
                      sourceSelector, preventOverlap) {
        cancelDrag();

        const span = minutes || slotMinutes;
        drag = {
            dotNetRef, kind, id, label,
            minutes: span,
            slotsNeeded: Math.max(1, Math.ceil(span / slotMinutes)),
            preventOverlap: !!preventOverlap,
            startX: clientX,
            startY: clientY,
            slotPixels,
            slotMinutes,
            source: sourceSelector ? document.querySelector(sourceSelector) : null,
            active: false,
            ghost: null,
            target: null,
            valid: false,
            copy: false,
        };

        window.addEventListener('pointermove', onDragMove);
        window.addEventListener('pointerup', onDragEnd);
        window.addEventListener('pointercancel', onDragAbort);
        window.addEventListener('keydown', onDragKey, true);
    }

    function onDragMove(e) {
        if (!drag) return;

        if (!drag.active) {
            const moved = Math.abs(e.clientX - drag.startX) + Math.abs(e.clientY - drag.startY);
            if (moved < DRAG_THRESHOLD_PX) return;
            activateDrag();
        }

        drag.copy = e.ctrlKey;
        edgeScroll(e.clientY);

        const slot = slotAt(e.clientX, e.clientY);
        drag.valid = slot !== null && isPlaceable(slot);
        setTarget(slot);
        positionGhost(e.clientX, e.clientY, slot);
    }

    /**
     * A block may only land where every slot it would cover is free. Slots the calendar
     * already has something in are marked data-busy by the Razor side.
     */
    function isPlaceable(slot) {
        if (!drag.preventOverlap) return true;

        const day = slot.dataset.day;
        const first = parseInt(slot.dataset.slot, 10);

        for (let i = 0; i < drag.slotsNeeded; i++) {
            const cell = document.querySelector(`.slot[data-day="${day}"][data-slot="${first + i}"]`);
            if (!cell) return false;                    // would run past the end of the day
            if (cell.dataset.busy === '1') return false;
        }

        return true;
    }

    function activateDrag() {
        drag.active = true;
        document.body.classList.add('is-dragging');
        if (drag.source) drag.source.classList.add('dragging');

        drag.ghost = document.createElement('div');
        drag.ghost.className = 'drag-ghost';
        drag.ghost.textContent = drag.label;
        document.body.appendChild(drag.ghost);
    }

    /** The slot under the pointer, looked through any allocation blocks sitting on top of it. */
    function slotAt(x, y) {
        const stack = document.elementsFromPoint(x, y);
        for (const element of stack) {
            if (element.dataset && element.dataset.slot !== undefined) return element;
        }
        return null;
    }

    function setTarget(slot) {
        if (drag.target === slot) return;
        if (drag.target) drag.target.classList.remove('drop-over', 'drop-blocked');
        drag.target = slot;
        if (slot) slot.classList.add(drag.valid ? 'drop-over' : 'drop-blocked');
    }

    function positionGhost(x, y, slot) {
        if (!drag.ghost) return;

        const height = Math.max(18, (drag.minutes / drag.slotMinutes) * drag.slotPixels - 2);
        drag.ghost.style.height = height + 'px';
        drag.ghost.classList.toggle('copying', drag.copy && drag.valid);
        drag.ghost.classList.toggle('blocked', slot !== null && !drag.valid);

        if (slot) {
            // Snap onto the grid so the preview shows exactly where it will land.
            const rect = slot.getBoundingClientRect();
            drag.ghost.style.left = (rect.left + 2) + 'px';
            drag.ghost.style.top = rect.top + 'px';
            drag.ghost.style.width = Math.max(40, rect.width - 4) + 'px';
        } else {
            drag.ghost.style.left = (x + 12) + 'px';
            drag.ghost.style.top = (y - 8) + 'px';
            drag.ghost.style.width = '220px';
        }
    }

    /** Scrolls the calendar when the pointer is held near its top or bottom edge. */
    function edgeScroll(clientY) {
        const scroller = document.querySelector('.cal-scroll');
        if (!scroller) return;

        const rect = scroller.getBoundingClientRect();
        if (clientY < rect.top + EDGE_SCROLL_ZONE_PX) {
            scroller.scrollTop -= EDGE_SCROLL_SPEED_PX;
        } else if (clientY > rect.bottom - EDGE_SCROLL_ZONE_PX) {
            scroller.scrollTop += EDGE_SCROLL_SPEED_PX;
        }
    }

    function onDragKey(e) {
        if (drag && e.key === 'Escape') {
            e.preventDefault();
            e.stopPropagation();
            onDragAbort();
        }
    }

    function onDragEnd(e) {
        if (!drag) return;

        const { dotNetRef, kind, id, active, target, valid } = drag;
        const copy = e.ctrlKey || drag.copy;
        const day = target ? target.dataset.day : null;
        const slot = target ? parseInt(target.dataset.slot, 10) : -1;

        teardownDrag();

        if (!active) return;

        // The pointer moved, so the click that follows was never meant as a click.
        swallowNextClick();

        if (!day || slot < 0) return;

        if (!valid) {
            dotNetRef.invokeMethodAsync('OnDropRejected');
            return;
        }

        dotNetRef.invokeMethodAsync('OnPointerDrop', kind, id, day, slot, copy);
    }

    function onDragAbort() {
        if (!drag) return;
        const wasActive = drag.active;
        teardownDrag();
        if (wasActive) swallowNextClick();
    }

    function teardownDrag() {
        if (!drag) return;

        if (drag.ghost) drag.ghost.remove();
        if (drag.target) drag.target.classList.remove('drop-over', 'drop-blocked');
        if (drag.source) drag.source.classList.remove('dragging');
        document.body.classList.remove('is-dragging');

        window.removeEventListener('pointermove', onDragMove);
        window.removeEventListener('pointerup', onDragEnd);
        window.removeEventListener('pointercancel', onDragAbort);
        window.removeEventListener('keydown', onDragKey, true);
        drag = null;
    }

    function cancelDrag() {
        teardownDrag();
    }

    /** Stops the click that Chromium fires after a pointer gesture from also being handled. */
    function swallowNextClick() {
        const swallow = e => {
            e.stopPropagation();
            e.preventDefault();
            window.removeEventListener('click', swallow, true);
        };
        window.addEventListener('click', swallow, true);
        // If no click materialises, drop the listener rather than eating a later one.
        setTimeout(() => window.removeEventListener('click', swallow, true), 400);
    }

    // ---------------------------------------------------------------- resize

    /**
     * Pointer-driven resize of an allocation block. The DOM is updated live for
     * responsiveness; .NET is told the final duration once on pointerup.
     */
    function startResize(dotNetRef, allocationId, startClientY, durationMinutes, slotPixels, slotMinutes) {
        const element = document.querySelector(`[data-alloc-id="${allocationId}"]`);
        if (!element) return;

        cancelResize();

        resize = {
            dotNetRef,
            allocationId,
            element,
            startClientY,
            startMinutes: durationMinutes,
            currentMinutes: durationMinutes,
            slotPixels,
            slotMinutes,
            pixelsPerMinute: slotPixels / slotMinutes,
        };

        element.classList.add('resizing');
        document.body.classList.add('is-resizing');
        window.addEventListener('pointermove', onResizeMove, { passive: true });
        window.addEventListener('pointerup', onResizeEnd);
        window.addEventListener('pointercancel', onResizeEnd);
    }

    function onResizeMove(e) {
        if (!resize) return;

        const deltaMinutes = (e.clientY - resize.startClientY) / resize.pixelsPerMinute;
        const raw = resize.startMinutes + deltaMinutes;
        const snapped = Math.max(resize.slotMinutes, Math.round(raw / resize.slotMinutes) * resize.slotMinutes);

        if (snapped === resize.currentMinutes) return;
        resize.currentMinutes = snapped;
        resize.element.style.height = (snapped * resize.pixelsPerMinute - 2) + 'px';
    }

    function onResizeEnd() {
        if (!resize) return;

        const { dotNetRef, allocationId, currentMinutes, startMinutes } = resize;
        cancelResize();
        swallowNextClick();

        if (currentMinutes !== startMinutes) {
            dotNetRef.invokeMethodAsync('OnResized', allocationId, currentMinutes);
        } else {
            // Nothing changed, but the inline height override has to go so Blazor owns it again.
            dotNetRef.invokeMethodAsync('OnResizeCancelled');
        }
    }

    function cancelResize() {
        if (!resize) return;
        resize.element.classList.remove('resizing');
        document.body.classList.remove('is-resizing');
        window.removeEventListener('pointermove', onResizeMove);
        window.removeEventListener('pointerup', onResizeEnd);
        window.removeEventListener('pointercancel', onResizeEnd);
        resize = null;
    }

    // ---------------------------------------------------------------- shortcuts

    function registerShortcuts(dotNetRef) {
        shortcutRef = dotNetRef;
        window.addEventListener('keydown', onKeyDown);
    }

    function unregisterShortcuts() {
        shortcutRef = null;
        window.removeEventListener('keydown', onKeyDown);
    }

    function onKeyDown(e) {
        if (!shortcutRef) return;

        // Never steal keys from a field the user is typing in.
        const tag = (e.target && e.target.tagName) || '';
        if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || e.target.isContentEditable) {
            if (e.key === 'Escape') e.target.blur();
            return;
        }

        const combo = (e.ctrlKey ? 'ctrl+' : '') + (e.shiftKey ? 'shift+' : '') + e.key.toLowerCase();
        const handled = [
            'arrowleft', 'arrowright', 't', 'r', 'delete', 'escape', 'f', '/', 'ctrl+s', 'ctrl+f',
        ];

        if (!handled.includes(combo)) return;
        e.preventDefault();
        shortcutRef.invokeMethodAsync('OnShortcut', combo);
    }

    // ---------------------------------------------------------------- misc

    function focusSelector(selector) {
        const element = document.querySelector(selector);
        if (element) element.focus();
    }

    /**
     * Where the caret is in a text box. The mention picker needs this to know which word is
     * being typed; Blazor only hands over the value, not the selection.
     */
    function caretIndex(selector) {
        const element = document.querySelector(selector);
        return element && typeof element.selectionStart === 'number' ? element.selectionStart : -1;
    }

    /** Puts the caret back after the component has rewritten the value around it. */
    function setCaret(selector, index) {
        const element = document.querySelector(selector);
        if (!element) return;

        element.focus();
        const at = Math.max(0, Math.min(index, element.value.length));
        element.setSelectionRange(at, at);
    }

    /** Scrolls the calendar so the given pixel offset sits near the top of the viewport. */
    function scrollCalendarTo(offsetPixels) {
        const scroller = document.querySelector('.cal-scroll');
        if (scroller) scroller.scrollTop = Math.max(0, offsetPixels - 60);
    }

    // ---------------------------------------------------------------- shell safety net

    // The shell fills the window and html/body are overflow:hidden, so the document itself
    // is never meant to scroll - every scrollable region is inside it. If something does
    // scroll it anyway (a focused control the browser decides to bring into view is the
    // usual way), the whole shell slides out of sight with no scrollbar left to bring it
    // back, and the window just goes blank. Put it straight back.
    window.addEventListener('scroll', () => {
        const root = document.scrollingElement;
        if (root && root.scrollTop !== 0) root.scrollTop = 0;
        if (root && root.scrollLeft !== 0) root.scrollLeft = 0;
    }, true);

    return {
        applyTheme,
        systemTheme,
        beginDrag,
        cancelDrag,
        startResize,
        cancelResize,
        registerShortcuts,
        unregisterShortcuts,
        focusSelector,
        caretIndex,
        setCaret,
        scrollCalendarTo,
    };
})();
