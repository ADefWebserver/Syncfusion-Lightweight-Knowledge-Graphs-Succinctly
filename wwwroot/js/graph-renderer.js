// Cytoscape.js interop for the knowledge graph explorer.
// Exposes exactly four functions on window.graphRenderer for Blazor to call.

(function () {
    const instances = new Map();

    function buildElements(graphData) {
        const elements = [];

        for (const node of graphData.nodes || []) {
            elements.push({
                data: {
                    id: node.id,
                    label: node.label,
                    type: node.type,
                },
            });
        }

        for (const edge of graphData.edges || []) {
            elements.push({
                data: {
                    id: edge.id,
                    source: edge.source,
                    target: edge.target,
                    label: edge.label,
                },
            });
        }

        return elements;
    }

    function graphStyle() {
        return [
            {
                selector: 'node',
                style: {
                    'label': 'data(label)',
                    'color': '#222',
                    'font-size': '10px',
                    'text-valign': 'center',
                    'text-halign': 'center',
                    'text-wrap': 'wrap',
                    'text-max-width': '90px',
                    'width': 46,
                    'height': 46,
                    'border-width': 1,
                    'border-color': '#ffffff',
                },
            },
            { selector: 'node[type = "Ticket"]', style: { 'background-color': '#4e79a7', 'shape': 'round-rectangle' } },
            { selector: 'node[type = "TicketDetail"]', style: { 'background-color': '#59a14f', 'shape': 'ellipse' } },
            { selector: 'node[type = "Requester"]', style: { 'background-color': '#e15759', 'shape': 'ellipse' } },
            { selector: 'node[type = "Status"]', style: { 'background-color': '#f28e2b', 'shape': 'diamond' } },
            { selector: 'node[type = "Day"]', style: { 'background-color': '#76b7b2', 'shape': 'rectangle' } },
            {
                selector: 'edge',
                style: {
                    'label': 'data(label)',
                    'font-size': '8px',
                    'color': '#555',
                    'width': 1.5,
                    'line-color': '#bbbbbb',
                    'target-arrow-color': '#bbbbbb',
                    'target-arrow-shape': 'triangle',
                    'curve-style': 'bezier',
                    'text-rotation': 'autorotate',
                    'text-background-color': '#ffffff',
                    'text-background-opacity': 0.8,
                    'text-background-padding': '2px',
                },
            },
            {
                selector: 'node:selected',
                style: {
                    'border-width': 4,
                    'border-color': '#111111',
                },
            },
            {
                selector: 'edge:selected',
                style: {
                    'width': 3,
                    'line-color': '#111111',
                    'target-arrow-color': '#111111',
                },
            },
        ];
    }

    function runLayout(cy) {
        cy.layout({ name: 'cose', animate: true }).run();
    }

    function initCytoscape(containerId, graphData, dotNetRef) {
        const existing = instances.get(containerId);
        if (existing) {
            existing.cy.destroy();
            instances.delete(containerId);
        }

        const container = document.getElementById(containerId);
        if (!container) {
            return;
        }

        const cy = cytoscape({
            container: container,
            elements: buildElements(graphData),
            style: graphStyle(),
            layout: { name: 'cose', animate: true },
        });

        cy.on('tap', 'node', function (evt) {
            const id = evt.target.id();
            dotNetRef.invokeMethodAsync('OnNodeClicked', id);
        });

        cy.on('tap', function (evt) {
            if (evt.target === cy) {
                cy.elements().unselect();
                dotNetRef.invokeMethodAsync('OnBackgroundClicked');
            }
        });

        instances.set(containerId, { cy: cy, dotNetRef: dotNetRef });
    }

    function updateGraph(containerId, graphData) {
        const entry = instances.get(containerId);
        if (!entry) {
            return;
        }

        const cy = entry.cy;
        cy.elements().remove();
        cy.add(buildElements(graphData));
        runLayout(cy);
    }

    function destroyCytoscape(containerId) {
        const entry = instances.get(containerId);
        if (!entry) {
            return;
        }

        entry.cy.destroy();
        if (entry.dotNetRef && typeof entry.dotNetRef.dispose === 'function') {
            // The .NET reference is disposed on the Blazor side; just drop it here.
        }
        instances.delete(containerId);
    }

    function toggleFullScreen(containerId) {
        const container = document.getElementById(containerId);
        if (!container) {
            return;
        }

        if (document.fullscreenElement) {
            document.exitFullscreen();
        } else {
            container.requestFullscreen();
        }
    }

    window.graphRenderer = {
        initCytoscape: initCytoscape,
        updateGraph: updateGraph,
        destroyCytoscape: destroyCytoscape,
        toggleFullScreen: toggleFullScreen,
    };
})();
