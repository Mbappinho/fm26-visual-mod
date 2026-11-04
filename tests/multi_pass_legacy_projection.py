"""Declared additive caller seams only; not feature approval."""
# No parsing/normalization of unknown source is permitted.
SEAMS = (
    ('    private OriginalPassDemo _originalPass;\n', '    private readonly MultiPassCommandHost _multiPass = new();\n'),
    ('(_originalPass?.OverlapActive ?? false)', ' || _multiPass.Blocked'),
    ('        _originalPass?.Tick();\n', '        _multiPass.Tick();\n'),
    ('        _originalPass?.Shutdown();\n', '        _multiPass.Shutdown();\n'),
    ('            ProcessSemanticCommand(root);\n', '            _multiPass.Process(root, _players, _semanticEvents, SemanticBlocked, SemanticOverlap);\n'),
)
# H154 FIFA overlay stage 1: second, independent all-or-none group of declared additive seams.
# Processed first (one anchor is itself the multi-pass addition). Exact anchor+addition, each exactly once.
FIFA_SEAMS = (
    ('            AddComponent<DiagnosticsBehaviour>();\n', '            AddComponent<FifaOverlayBehaviour>(); // H154: idle unless an opt-in FIFA overlay command is accepted.\n'),
    (' || _multiPass.Blocked', ' || FifaOverlayHost.Blocked'),
    ('            _multiPass.Process(root, _players, _semanticEvents, SemanticBlocked, SemanticOverlap);\n', '            FifaOverlayHost.Process(root, _players, SemanticBlocked, _candidateOverlay != null || _prototype != null, WriteJson);\n'),
    ('                _candidateOverlay?.Stop("match-root-changed");\n', '                FifaOverlayHost.Stop("match-root-changed");\n'),
    ('                _candidateOverlay?.Stop("diagnostic-error-limit");\n', '                FifaOverlayHost.Stop("diagnostic-error-limit");\n'),
)
def remove_fifa_seams(current):
    text=current
    presence=[addition in text for _,addition in FIFA_SEAMS]
    if any(presence) and not all(presence):
        raise ValueError('incomplete FIFA opt-in seam set')
    if all(presence):
        for anchor, addition in FIFA_SEAMS:
            if text.count(anchor+addition)!=1 or text.count(addition)!=1:
                raise ValueError('absent, duplicate or displaced FIFA opt-in seam')
            text=text.replace(anchor+addition,anchor)
    return text
def legacy_caller_projection(current, frozen):
    text=remove_fifa_seams(current)
    presence=[addition in text for _,addition in SEAMS]
    if any(presence) and not all(presence):
        raise ValueError('incomplete opt-in seam set')
    if all(presence):
        for anchor, addition in SEAMS:
            count=2 if '_originalPass?.Tick();' in anchor else 1
            if text.count(anchor+addition)!=count or text.count(addition)!=count:
                raise ValueError('absent, duplicate or displaced opt-in seam')
            text=text.replace(anchor+addition,anchor)
    for stage in ('Update','LateUpdate'):
        assignment='        _semanticEvents.SetCallbackStage("'+stage+'");\n'
        if text.count(assignment)!=1 or text.count(assignment+'        _semanticEvents.Tick(SemanticOverlap);')!=1:
            raise ValueError('missing or duplicate callback stage')
        text=text.replace(assignment,'')
    if text != frozen:
        raise ValueError("unauthorized legacy caller change")
    return text
