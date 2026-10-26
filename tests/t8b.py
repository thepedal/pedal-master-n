# Clicks on the switch-on moments added by section skipping in the ReBuzz engine:
# a section that was off or flat starts from rest when it becomes active again.
# Same measure as t8: largest second difference after the change over the steady one
# (about 1 = nothing happens; a click would read far higher).
from lib import *
sr=48000; x=sine(220,-12,1.0,sr); y=sine(330,-12,1.0,sr)
def click(changes,**kw):
    L,R,lat,_=run(x,y,sr,at=changes,**kw)
    d2=np.abs(np.diff(L,2)); base=np.max(d2[lat+2000:24000-100])
    return np.max(d2[24000:])/base
cases={
 'EQ on again (low +12 dB)':     ([(24000,'EQ',1)],   dict(EQ=0,LOWGAIN=240,LIMITER=0)),
 'EQ off→on, low cut 250 Hz':    ([(24000,'EQ',1)],   dict(EQ=0,LOWCUT=100,LIMITER=0)),
 'Tilt 0→+6 dB':                 ([(24000,'TILT',120)], dict(LIMITER=0)),
 'High gain 0→+12 dB':           ([(24000,'HIGHGAIN',240)], dict(LIMITER=0)),
 'High gain 0→-12 dB':           ([(24000,'HIGHGAIN',0)], dict(LIMITER=0)),
 'Low gain +12→0 then →+12':     ([(12000,'LOWGAIN',120),(24000,'LOWGAIN',240)], dict(LOWGAIN=240,LIMITER=0)),
 'Low Mono off→on→off→on':       ([(12000,'LOWMONO',0),(24000,'LOWMONO',100)], dict(LOWMONO=100)),
 'Comp off→on, 0.3 s release':   ([(24000,'COMP',1)], dict(THRESH=20,RATIO=5,RELEASE=1,LIMITER=0)),
}
for k,(c,kw) in cases.items(): print(f'{k:34s} max |Δ²| after change / steady = {click(c,**kw):.2f}')
