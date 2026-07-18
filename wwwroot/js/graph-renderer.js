// Knowledge-graph renderer using Cytoscape.js (ported from AIStoryBuildersGraph).
// Node types are strings that match the help-desk graph model:
// Ticket, TicketDetail, Requester, Status, Day.

// Map node type -> visual style.
var _nodeStyleMap = {
    'Ticket': { color: '#4e79a7', shape: 'round-rectangle' },
    'TicketDetail': { color: '#59a14f', shape: 'ellipse' },
    'Requester': { color: '#e15759', shape: 'ellipse' },
    'Status': { color: '#f28e2b', shape: 'diamond' },
    'Day': { color: '#76b7b2', shape: 'rectangle' }
};

var _cytoscapeStyles = [
    {
        selector: 'node',
        style: {
            'label': 'data(label)',
            'text-valign': 'center',
            'text-halign': 'center',
            'font-size': '10px',
            'color': '#fff',
            'text-outline-width': 2,
            'text-outline-color': '#555',
            'width': 45,
            'height': 45,
            'background-color': '#9c755f',
            'shape': 'ellipse',
            'text-wrap': 'ellipsis',
            'text-max-width': '90px'
        }
    },
    { selector: 'node[nodeType = "Ticket"]',       style: { 'background-color': '#4e79a7', 'shape': 'round-rectangle', 'width': 60, 'height': 42 } },
    { selector: 'node[nodeType = "TicketDetail"]', style: { 'background-color': '#59a14f', 'shape': 'ellipse', 'width': 30, 'height': 30, 'font-size': '8px' } },
    { selector: 'node[nodeType = "Requester"]',    style: { 'background-color': '#e15759', 'shape': 'ellipse', 'width': 52, 'height': 52 } },
    { selector: 'node[nodeType = "Status"]',       style: { 'background-color': '#f28e2b', 'shape': 'diamond', 'width': 55, 'height': 55 } },
    { selector: 'node[nodeType = "Day"]',          style: { 'background-color': '#76b7b2', 'shape': 'rectangle', 'width': 55, 'height': 40 } },
    {
        selector: 'edge',
        style: {
            'label': 'data(label)',
            'font-size': '9px',
            'color': '#333',
            'text-background-color': '#fff',
            'text-background-opacity': 0.85,
            'text-background-padding': '2px',
            'text-rotation': 'autorotate',
            'width': 2,
            'line-color': '#bbb',
            'target-arrow-color': '#bbb',
            'target-arrow-shape': 'triangle',
            'curve-style': 'bezier'
        }
    },
    {
        selector: ':selected',
        style: {
            'border-width': 3,
            'border-color': '#E74C3C'
        }
    }
];

var _cytoscapeLayout = {
    name: 'cose',
    animate: true,
    animationDuration: 800,
    nodeRepulsion: function () { return 20000; },
    idealEdgeLength: function () { return 120; },
    nodeOverlap: 20,
    padding: 50
};

function _buildNodes(graphData) {
    return (graphData.nodes || []).map(function (n) {
        return {
            group: 'nodes',
            data: {
                id: n.id,
                label: n.label,
                nodeType: n.type
            }
        };
    });
}

function _buildEdges(graphData) {
    return (graphData.edges || []).map(function (e) {
        return {
            group: 'edges',
            data: {
                id: e.id,
                source: e.source,
                target: e.target,
                label: e.label
            }
        };
    });
}

window.initCytoscape = function (containerId, graphData, dotNetRef) {
    var container = document.getElementById(containerId);
    if (!container) return;

    if (container._cyInstance) {
        container._cyInstance.destroy();
        container._cyInstance = null;
    }

    var nodes = _buildNodes(graphData);
    var edges = _buildEdges(graphData);

    var cy = cytoscape({
        container: container,
        elements: nodes.concat(edges),
        style: _cytoscapeStyles,
        layout: _cytoscapeLayout,
        userZoomingEnabled: true,
        userPanningEnabled: true,
        boxSelectionEnabled: false
    });

    // Click node: highlight and notify Blazor.
    cy.on('tap', 'node', function (evt) {
        cy.nodes().unselect();
        evt.target.select();
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnNodeClicked', evt.target.data('id'));
        }
    });

    // Click background: deselect and dismiss panel.
    cy.on('tap', function (evt) {
        if (evt.target === cy) {
            cy.nodes().unselect();
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('OnNodeClicked', null);
            }
        }
    });

    container._cyInstance = cy;
    container._dotNetRef = dotNetRef;
};

window.updateGraph = function (containerId, graphData) {
    var container = document.getElementById(containerId);
    if (!container || !container._cyInstance) return;

    var cy = container._cyInstance;
    var nodes = _buildNodes(graphData);
    var edges = _buildEdges(graphData);

    cy.elements().remove();
    cy.add(nodes.concat(edges));
    cy.layout(_cytoscapeLayout).run();
};

window.toggleFullScreen = function (containerId) {
    var container = document.getElementById(containerId);
    if (!container) return;

    if (!document.fullscreenElement) {
        container.requestFullscreen().then(function () {
            if (container._cyInstance) {
                setTimeout(function () {
                    container._cyInstance.resize();
                    container._cyInstance.fit();
                }, 100);
            }
        });
    } else {
        document.exitFullscreen();
    }
};

document.addEventListener('fullscreenchange', function () {
    var el = document.fullscreenElement || document.getElementById('cy');
    if (el && el._cyInstance) {
        setTimeout(function () {
            el._cyInstance.resize();
            el._cyInstance.fit();
        }, 100);
    }
});

window.destroyCytoscape = function (containerId) {
    var container = document.getElementById(containerId);
    if (container && container._cyInstance) {
        container._cyInstance.destroy();
        container._cyInstance = null;
        container._dotNetRef = null;
    }
};
