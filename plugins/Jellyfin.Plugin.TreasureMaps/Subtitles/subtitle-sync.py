"""Private stdin protocol. No network, credentials, or source paths in the result."""
import json
import logging
import math
import os
import sys


def synchronize(request):
    import numpy as np
    from ffsubsync.ffsubsync import make_parser, make_reference_pipe, try_sync, get_srt_pipe_maker

    logging.disable(logging.CRITICAL)
    if hasattr(os, 'nice'):
        os.nice(10)
    args = make_parser().parse_args([
        request['video'], '-i', request['input'], '-o', request['output'],
        '--reference-stream', '0:a:' + str(request['audio']),
        '--ffmpeg-path', os.path.dirname(request['ffmpeg']),
        '--max-offset-seconds', '120', '--skip-infer-framerate-ratio',
        '--output-encoding', 'utf-8',
    ])
    reference = make_reference_pipe(args)
    reference.fit(args.reference)
    speech = reference.transform(args.reference)
    if len(speech) < 3000 or np.count_nonzero(speech > 0) < 1000:
        raise ValueError('insufficient speech')
    result = {}
    if not try_sync(args, reference, result):
        raise ValueError('no alignment')
    offset, scale = result['offset_seconds'], result['framerate_scale_factor']
    if not math.isfinite(offset) or abs(offset) > 120 or not 0.94 < scale < 1.06:
        raise ValueError('unsafe alignment')
    # Validate actual speech/subtitle overlap; the tool's success flag alone is
    # not a confidence score. Music, silence and unrelated subtitles must fail closed.
    aligned = get_srt_pipe_maker(args, request['output'])(1.0).fit_transform(request['output'])
    size = min(len(speech), len(aligned))
    correlation = float(np.corrcoef(speech[:size], aligned[:size])[0, 1])
    if not math.isfinite(correlation) or correlation < 0.12:
        raise ValueError('weak speech alignment')
    return {'ok': True, 'offsetSeconds': offset, 'scale': scale, 'correlation': round(correlation, 3)}


if __name__ == '__main__':
    try:
        print(json.dumps(synchronize(json.load(sys.stdin))))
    except Exception:
        print(json.dumps({'ok': False, 'error': 'alignment_unreliable'}))
        sys.exit(1)
