# How to run:
#   python -m venv .venv
#   . .venv/bin/activate   # Windows: .venv\Scripts\activate
#   pip install flask
#   python app_single.py

from __future__ import annotations
from flask import Flask, jsonify, request, Response
import threading, time, random

# ---------------- Tunable settings ----------------
DT = 0.5                # control loop period (seconds)
TARGET_MIN = 0.5        # mg/L
TARGET_MAX = 1.5        # mg/L
SETPOINT = 1.0          # mg/L (fixed for simplicity)
TRIP_HIGH = 2.0         # mg/L threshold
TRIP_HOLD_S = 10        # seconds above TRIP_HIGH to trip (use 60 for stricter)
SENSOR_DISAGREE = 0.4   # mg/L difference to declare disagreement

# ---------------- Tiny plant + sensors -------------
class Plant:
    def __init__(self):
        self.C = 1.0          # residual mg/L
        self.flow = 1.0       # relative units
        self.valve_open = True

    def step(self, dose: float, dt: float):
        if not self.valve_open:
            dose = 0.0
        # first-order-ish response with flow influence
        k = 0.08
        alpha = 0.6
        beta = 0.2
        # small random flow wiggle
        self.flow = min(2.0, max(0.2, self.flow + random.uniform(-0.02, 0.02)))
        dC = -k*self.C + alpha*dose - beta*(self.flow-1.0)
        self.C = max(0.0, self.C + dC*dt)

class Sensor:
    def __init__(self, name):
        self.name = name
        self.drift = 0.0
        self.noise = 0.03

    def read(self, trueC: float) -> float:
        noise = random.gauss(0, self.noise)
        return max(0.0, trueC + self.drift + noise)

class PID:
    def __init__(self, kp=0.7, ki=0.2):
        self.kp, self.ki = kp, ki
        self.i = 0.0
        self.prev_out = 0.0

    def step(self, setpt: float, meas: float, dt: float) -> float:
        err = setpt - meas
        self.i += err * dt
        raw = self.kp*err + self.ki*self.i
        raw = max(0.0, min(2.5, raw))  # clip
        # rate-limit to avoid sudden jumps (+0.2 mg/L per 30s equivalent)
        max_step = 0.2/30 * dt
        out = self.prev_out + max(-max_step, min(max_step, raw - self.prev_out))
        self.prev_out = out
        return out

# ---------------- Global state ----------------
plant = Plant()
pid = PID()
sensorA = Sensor("A")
sensorB = Sensor("B")

state = {
    "mode": "NORMAL",       # NORMAL | DEGRADED | TRIPPED
    "setpoint": SETPOINT,
    "dose": 0.0,
    "residual_A": 1.0,
    "residual_B": 1.0,
    "residual_validated": 1.0,
    "sensor_disagree": False,
    "stale": False
}

# Fault toggles (R1..R6)
faults = {
    "R1_driftA": False,     # sensor A positive drift
    "R2_flowRamp": False,   # rapid flow increase
    "R3_netStale": False,   # return previous snapshot from /state
    "R4_valveStuck": False, # valve fails to open
    "R5_alarmStorm": False, # placeholder
    "R6_replay": False      # validated measurement frozen near normal
}
_last_snapshot = None

# ---------------- Control loop thread ----------------
def control_loop():
    global _last_snapshot
    hi_timer = 0.0
    while True:
        t0 = time.time()

        # Fault effects
        sensorA.drift = 0.35 if faults["R1_driftA"] else 0.0
        if faults["R2_flowRamp"]:
            plant.flow = min(2.0, plant.flow + 0.15)

        # Measurements
        trueC = plant.C
        A = sensorA.read(trueC)
        B = sensorB.read(trueC)
        disagree = abs(A - B) > SENSOR_DISAGREE

        # Validate (very simple demo logic)
        validated = 0.5*(A + B) if not disagree else min(A, B)
        if faults["R6_replay"]:
            validated = 1.0

        # Mode
        if state["mode"] != "TRIPPED":
            state["mode"] = "DEGRADED" if disagree else "NORMAL"

        # Trip check
        if validated >= TRIP_HIGH and state["mode"] != "TRIPPED":
            hi_timer += DT
            if hi_timer >= TRIP_HOLD_S:
                state["mode"] = "TRIPPED"
        else:
            hi_timer = 0.0

        # Controller and plant step
        dose = 0.0 if state["mode"] == "TRIPPED" else pid.step(SETPOINT, validated, DT)
        plant.valve_open = (not faults["R4_valveStuck"]) and (state["mode"] != "TRIPPED")
        plant.step(dose, DT)

        # Publish state
        state.update({
            "residual_A": A,
            "residual_B": B,
            "residual_validated": validated,
            "sensor_disagree": disagree,
            "dose": dose,
            "stale": faults["R3_netStale"]
        })
        _last_snapshot = dict(state)

        # pacing
        dt = time.time() - t0
        time.sleep(max(0.0, DT - dt))

# Start the background loop
threading.Thread(target=control_loop, daemon=True).start()

# ---------------- Flask app + inline UI ----------------
app = Flask(__name__)

@app.get("/state")
def get_state():
    if faults["R3_netStale"] and _last_snapshot is not None:
        return jsonify(_last_snapshot)
    return jsonify(state)

@app.post("/fault")
def set_fault():
    data = request.get_json(force=True, silent=True) or {}
    key = data.get("key"); on = bool(data.get("on"))
    if key not in faults:
        return jsonify({"ok": False, "error": "unknown_fault"}), 400
    faults[key] = on
    return jsonify({"ok": True, "faults": faults})

# Inline HTML/JS/CSS served from root
INDEX_HTML = r"""<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>SafeChlor – single-file prototype</title>
  <style>
    :root{--border:#ddd;--muted:#666}
    body{font-family:system-ui,Segoe UI,Roboto,Arial,sans-serif;margin:24px;max-width:1100px}
    h1{font-size:40px;margin:0 0 10px}
    .lead{color:#333;margin:0 0 18px}
    code{background:#f3f3f3;padding:2px 6px;border-radius:6px}
    .grid{display:grid;grid-template-columns:1fr 1fr;gap:16px}
    .card{border:2px solid var(--border);border-radius:12px;padding:16px}
    .badge{display:inline-block;padding:2px 8px;border-radius:999px;background:#eee;margin-right:6px}
    .err{color:#b00020}
    label{display:block;margin:8px 0}
    input[type=checkbox]{transform:scale(1.15);margin-right:8px}
    .muted{color:var(--muted)}
  </style>
</head>
<body>
  <h1>SafeChlor – single-file prototype</h1>
  <p class="lead">Backend loop ticks every <code>0.5s</code>. Toggle faults to see <b>NORMAL → DEGRADED → TRIPPED</b>.</p>

  <div class="grid">
    <div class="card">
      <div>Mode: <b id="mode">—</b></div>
      <div>Residual A: <span id="a">—</span> mg/L</div>
      <div>Residual B: <span id="b">—</span> mg/L</div>
      <div>Validated: <span id="val">—</span> mg/L</div>
      <div>Dose: <span id="dose">—</span> mg/L</div>
      <div id="flags" style="margin-top:8px"></div>
      <div id="err" class="err"></div>
    </div>
    <div class="card">
      <div><b>Faults</b> (click to toggle)</div>
      <div id="faults"></div>
      <p class="muted">
        R1: sensor A drift; R2: fast flow ramp; R3: stale <code>/state</code> output;
        R4: valve stuck; R6: validated residual frozen near 1.0.
      </p>
    </div>
  </div>

<script>
  const API = ''; // same-origin

  const keys = ["R1_driftA","R2_flowRamp","R3_netStale","R4_valveStuck","R5_alarmStorm","R6_replay"];
  const wrap = document.getElementById('faults');
  keys.forEach(k => {
    const id = 'f_'+k;
    const label = document.createElement('label');
    const cb = document.createElement('input'); cb.type='checkbox'; cb.id=id;
    cb.addEventListener('change', ()=>toggleFault(k, cb.checked));
    label.appendChild(cb); label.append(' ' + k); wrap.appendChild(label);
  });

  async function toggleFault(key, on){
    try{
      await fetch(API + '/fault', {
        method:'POST',
        headers:{'Content-Type':'application/json'},
        body: JSON.stringify({key, on})
      });
    }catch(e){ console.error(e); }
  }

  const elMode = document.getElementById('mode');
  const elA = document.getElementById('a');
  const elB = document.getElementById('b');
  const elVal = document.getElementById('val');
  const elDose = document.getElementById('dose');
  const elFlags = document.getElementById('flags');
  const elErr = document.getElementById('err');

  function badge(txt){
    const b = document.createElement('span'); b.className='badge'; b.textContent=txt;
    elFlags.appendChild(b);
  }

  async function tick(){
    try{
      const res = await fetch(API + '/state', {cache:'no-store'});
      if(!res.ok) throw new Error('HTTP '+res.status);
      const s = await res.json();
      elErr.textContent = '';
      elMode.textContent = s.mode || '—';
      elA.textContent = (s.residual_A ?? 0).toFixed(2);
      elB.textContent = (s.residual_B ?? 0).toFixed(2);
      elVal.textContent = (s.residual_validated ?? 0).toFixed(2);
      elDose.textContent = (s.dose ?? 0).toFixed(2);
      elFlags.innerHTML = '';
      if (s.sensor_disagree) badge('SensorDisagree');
      if (s.stale) badge('Stale');
      if (s.mode === 'TRIPPED') badge('TRIPPED');
    }catch(e){
      elErr.textContent = 'Cannot fetch /state: ' + e.message + '. Is app running?';
      console.error(e);
    }
  }
  setInterval(tick, 500);
  tick();
</script>
</body>
</html>
"""

@app.get("/")
def home():
    return Response(INDEX_HTML, mimetype="text/html")

if __name__ == "__main__":
    print("Open http://127.0.0.1:8000")
    app.run(host="127.0.0.1", port=8000, debug=True)
