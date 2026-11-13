"""
extract_chat_history.py

Extracts all readable conversation text, user prompts, assistant thoughts,
tool calls, outputs, Markdown artifacts, AND comprehensive step-by-step
model and token accounting (with dynamic turn-level model switch pricing)
related to VSOnDemandSymbols from Antigravity's conversation database
and brain storage into C:\\Dev\\VSOnDemandSymbols\\chat_history.

Creates:
  - chat_history/<session_folder>/conversation.md
  - chat_history/<session_folder>/session_metadata.json
  - chat_history/<session_folder>/artifacts/*
  - chat_history/artifacts/* (central copy)
  - chat_history/summary_metrics.json (100% self-contained analysis dataset)
  - chat_history/INDEX.md & README.md (rich master catalog)
"""

import os
import re
import sys
import json
import shutil
import sqlite3
from datetime import datetime, timezone

CONV_DIR = r"C:\Users\matt\.gemini\antigravity\conversations"
BRAIN_DIR = r"C:\Users\matt\.gemini\antigravity\brain"
OUTPUT_DIR = r"C:\Dev\VSOnDemandSymbols\chat_history"
CENTRAL_ARTIFACTS_DIR = os.path.join(OUTPUT_DIR, "artifacts")

# Known main sessions dedicated to chat-history extraction, process analysis, and slide deck creation
META_SESSION_IDS = {
    "d3d910fd-56f4-4ba5-96b1-7201a18debbf",  # S50: extract_chat_history.py & ANALYSIS_PLAN / DIMENSION_*
    "cc9b6218-2add-4ddb-929c-8c235974389e",  # S52: slide deck creation
    "8aab4909-a978-4306-a246-9fd7170b74b5",  # S58: presentation.html review
    "0890d259-ddf6-44a3-8aab-4801579dbc2f",  # S59: presentation_rules.md review
    "ddb7a4e5-20ac-47ee-8a41-60a7c9ddfe5d",  # S60: re-run extract_chat_history & split meta sessions
    "d4adee65-8891-495d-aba9-1d82f7af4434",  # S62: dimension 4 time analysis
    "425584b7-a7bf-4f00-aa26-601da6afbd92",  # S63: update presentation with unanswered questions
    "f9e84bf8-a7d2-454f-9413-5e4e26445bb3",  # S64: learnings from time analysis
    "207fab11-e74e-4cbe-94b1-734f24b18fed",  # S65: update self-reflection stats in presentation
    "ae5026a2-4d7f-43b9-9181-86f1277605d2",  # S66: research ethical concerns about AI coding
    "e4b80ab5-0c0e-4490-aa93-32bb151c7812",  # S67: improve presentation grid view
    "b40c261d-06b3-4c89-98eb-421ea6fdb8f9",  # S68: ~500m token spend equivalence
    "7465b370-484f-4203-ae73-d3316127069a",  # S69: add next slide button to deck
    "52611b4f-7da2-4721-9237-2028e886e583",  # S70: check $12 claim on slide 7
    "8a54109e-6cce-4042-87ea-175a6026b8cf",  # S71: improve presentation title page
    "0f120f77-4e79-46ac-ab3b-2fd97e801021",  # S72: combine slides 19 and 20
    "95d0410d-7ff7-436a-a8d9-92677aec6500",  # S73: find Airplane! / Flash Gordon images (+7 subagents)
    "76566b18-d600-45d9-b811-89376bcc2ac7",  # S81: better use of horizontal space on slides
    "899b4a7e-162d-4f83-9c0e-7e9dc181dc04",  # S82: make slide deck mobile friendly
    "44033a64-e16a-4a49-a7a2-fe836f7595ea",  # S83: add share button to slide deck
    "aafe59be-ae47-47f5-ab67-dc6de9644ac6",  # S84: update META_ANALYSIS.md
    "a7d71f44-04e8-4d70-a87d-e483be8bd56a",  # S85: expand slide 25 details
}

META_PROMPT_PATTERNS = (
    "extract_chat_history",
    "slide_deck_plan",
    "presentation_rules",
    "presentation.html",
    "the chat_history folder contains",
)

def classify_session_category(cid, parent_id, first_prompt, meta_ids):
    if cid in meta_ids or (parent_id and parent_id in meta_ids):
        return "meta"
    p_low = (first_prompt or "").lower()
    if any(pat in p_low for pat in META_PROMPT_PATTERNS):
        return "meta"
    return "development"

# Standard token rates per million tokens
RATES = {
    'opus': {'in': 15.0, 'cache': 3.75, 'out': 75.0},
    'argon': {'in': 1.25, 'cache': 0.30, 'out': 5.0},
    'hydrogen': {'in': 0.80, 'cache': 0.20, 'out': 3.20},
    'flash': {'in': 0.10, 'cache': 0.025, 'out': 0.40}
}

def get_model_tier(model_name):
    m = (model_name or '').lower()
    if 'opus' in m:
        return 'opus'
    if 'argon' in m or 'pro' in m:
        return 'argon'
    if 'hydrogen' in m:
        return 'hydrogen'
    return 'flash'

def calculate_step_cost_usd(model_name, in_tok, cache_tok, out_tok):
    tier = get_model_tier(model_name)
    r = RATES[tier]
    return (in_tok * r['in'] + cache_tok * r['cache'] + out_tok * r['out']) / 1_000_000.0

def clean_user_prompt(text):
    if not text:
        return "", ""
    req_match = re.search(r"<USER_REQUEST>(.*?)</USER_REQUEST>", text, re.DOTALL)
    if req_match:
        prompt_body = req_match.group(1).strip()
        meta_match = re.search(r"<ADDITIONAL_METADATA>(.*?)</ADDITIONAL_METADATA>", text, re.DOTALL)
        meta_str = meta_match.group(1).strip() if meta_match else ""
        return prompt_body, meta_str
    
    sys_match = re.search(r"<SYSTEM_MESSAGE>(.*?)</SYSTEM_MESSAGE>", text, re.DOTALL)
    if sys_match:
        return sys_match.group(1).strip(), ""
        
    return text.strip(), ""

def make_slug(text, max_len=36):
    if not text:
        return "session"
    clean = re.sub(r"[^\w\s-]", "", text).strip().lower()
    clean = re.sub(r"[-\s]+", "-", clean)
    slug = clean[:max_len].rstrip("-")
    return slug or "session"

def format_tool_call(tool):
    name = tool.get("name") or tool.get("function", {}).get("name") or "unknown_tool"
    args = tool.get("args") or tool.get("function", {}).get("arguments") or {}
    if isinstance(args, str):
        try:
            args = json.loads(args)
        except Exception:
            pass
            
    summary = ""
    details = ""
    if name == "run_command":
        cmd = args.get("CommandLine", "")
        summary = args.get("toolSummary", "")
        details = f"```powershell\n{cmd}\n```"
    elif name in ["read_file", "write_file", "edit_file"]:
        path = args.get("Path", "")
        summary = f"{name}: {os.path.basename(path)}"
        if name == "edit_file":
            target = args.get("Target", "")
            repl = args.get("Replacement", "")
            target_prev = target if len(target) <= 200 else target[:200] + "..."
            repl_prev = repl if len(repl) <= 200 else repl[:200] + "..."
            details = f"**File:** `{path}`\n\n```diff\n- Target:\n{target_prev}\n+ Replacement:\n{repl_prev}\n```"
        elif name == "write_file":
            content = args.get("Content", "")
            first_lines = "\n".join(content.splitlines()[:10])
            details = f"**File:** `{path}`\n\n```\n{first_lines}\n... ({len(content.splitlines())} lines total)\n```"
        else:
            details = f"**File:** `{path}`"
    elif name == "invoke_subagent":
        role = args.get("Role", "")
        prompt = args.get("Prompt", "")
        summary = f"Invoke Subagent: {role}"
        details = f"**Role:** {role}\n\n**Prompt:**\n> {prompt}"
    elif name == "search_web":
        query = args.get("query", "")
        summary = f"search_web: {query}"
        details = f"**Query:** `{query}`"
    elif name == "schedule":
        prompt = args.get("Prompt", "")
        dur = args.get("DurationSeconds", "")
        cron = args.get("CronExpression", "")
        summary = f"schedule: {cron or (str(dur) + 's')}"
        details = f"**Schedule:** `{cron or dur}`\n**Prompt:** `{prompt}`"
    else:
        summary = name
        try:
            details = f"```json\n{json.dumps(args, indent=2)}\n```"
        except Exception:
            details = f"```\n{str(args)}\n```"
            
    return name, summary, details

def format_tool_output(content):
    if not content:
        return ""
    lines = content.splitlines()
    if len(lines) <= 12 and len(content) <= 800:
        return f"```\n{content.strip()}\n```\n"
    
    preview = "\n".join(lines[:10])
    return (
        f"```\n{preview}\n...\n```\n\n"
        f"<details>\n<summary>▶ Show Full Output ({len(lines)} lines)</summary>\n\n"
        f"```\n{content.strip()}\n```\n\n"
        f"</details>\n"
    )

def collect_vson_demand_sessions():
    """Identify all sessions belonging to VSOnDemandSymbols."""
    matching_cids = set()

    # 1. Scan SQLite DB metadata
    if os.path.exists(CONV_DIR):
        for f in os.listdir(CONV_DIR):
            if f.endswith(".db"):
                cid = f[:-3]
                db_path = os.path.join(CONV_DIR, f)
                try:
                    conn = sqlite3.connect(db_path)
                    c = conn.cursor()
                    c.execute("SELECT data FROM trajectory_metadata_blob")
                    row = c.fetchone()
                    if row and row[0] and b"VSOnDemandSymbols" in row[0]:
                        matching_cids.add(cid)
                    conn.close()
                except Exception:
                    pass

    # 2. Check brain transcripts
    if os.path.exists(BRAIN_DIR):
        for cid in os.listdir(BRAIN_DIR):
            b_path = os.path.join(BRAIN_DIR, cid)
            if not os.path.isdir(b_path):
                continue
            t_path = os.path.join(b_path, ".system_generated", "logs", "transcript_full.jsonl")
            if not os.path.exists(t_path):
                t_path = os.path.join(b_path, ".system_generated", "logs", "transcript.jsonl")
            if os.path.exists(t_path):
                try:
                    with open(t_path, "rb") as f:
                        header = f.read(150000)
                        if b"VSOnDemandSymbols" in header:
                            matching_cids.add(cid)
                except Exception:
                    pass

    # First pass: sort raw sessions chronologically
    raw_sessions = []
    for cid in matching_cids:
        b_path = os.path.join(BRAIN_DIR, cid)
        t_full = os.path.join(b_path, ".system_generated", "logs", "transcript_full.jsonl")
        t_path = t_full if os.path.exists(t_full) else os.path.join(b_path, ".system_generated", "logs", "transcript.jsonl")
        first_time = None
        if os.path.exists(t_path):
            with open(t_path, "r", encoding="utf-8", errors="replace") as f:
                for line in f:
                    try:
                        o = json.loads(line)
                        t = o.get("created_at")
                        if t:
                            first_time = t
                            break
                    except Exception:
                        pass
        raw_sessions.append((first_time or "1970-01-01T00:00:00Z", cid, t_path, b_path))

    raw_sessions.sort(key=lambda s: s[0])

    # Second pass: step-by-step turn and model tracking with session continuity
    sessions = []
    prev_session_model = "Gemini 3.8 Flash (Medium)"

    for first_time_candidate, cid, t_path, b_path in raw_sessions:
        first_time = None
        last_time = None
        first_prompt_clean = ""
        parent_id = None
        user_prompts_count = 0
        model_responses_count = 0
        step_0_sender = None
        
        current_model = prev_session_model
        model_transitions = []
        per_model_usage = {}
        step_timestamps = []
        
        input_tokens = 0
        output_tokens = 0
        cache_read_tokens = 0
        thinking_chars = 0
        thinking_steps = 0
        tools_by_name = {}
        tool_errors_count = 0
        compactions_count = 0
        total_steps = 0
        session_cost_usd = 0.0
        
        if os.path.exists(t_path):
            with open(t_path, "r", encoding="utf-8", errors="replace") as f:
                for line_idx, line in enumerate(f):
                    total_steps += 1
                    try:
                        obj = json.loads(line)
                        t_str = obj.get("created_at")
                        if t_str:
                            if first_time is None:
                                first_time = t_str
                            last_time = t_str
                            step_timestamps.append((t_str, current_model))
                        stype = obj.get("type")
                        content = obj.get("content", "")
                        
                        if stype == "USER_INPUT":
                            user_prompts_count += 1
                            if not first_prompt_clean:
                                body, _ = clean_user_prompt(content)
                                first_prompt_clean = body
                                
                            # Check strict USER_SETTINGS_CHANGE
                            settings_match = re.search(r"<USER_SETTINGS_CHANGE>(.*?)</USER_SETTINGS_CHANGE>", content, re.DOTALL)
                            if settings_match:
                                s_text = settings_match.group(1)
                                m = re.search(r"to\s+(.+?)(?:\.\s+No need|\.\s*$|\n|$)", s_text)
                                if m:
                                    new_mod = m.group(1).strip("`").strip().rstrip(".")
                                    if new_mod and new_mod != current_model:
                                        current_model = new_mod
                                        model_transitions.append((line_idx, t_str or "", current_model))
                                        
                        elif stype == "SYSTEM_MESSAGE":
                            m = re.search(r"sender=([0-9a-fA-F-]+)", content)
                            if m:
                                if line_idx == 0:
                                    step_0_sender = m.group(1)
                                if parent_id is None:
                                    parent_id = m.group(1)
                            if not first_prompt_clean and line_idx == 0:
                                m_body = re.search(r"content=(.*)", content, re.DOTALL)
                                if m_body:
                                    first_prompt_clean = m_body.group(1).strip()
                        elif stype == "CHECKPOINT":
                            compactions_count += 1
                        elif stype == "PLANNER_RESPONSE":
                            in_t = obj.get("input_tokens", 0)
                            out_t = obj.get("output_tokens", 0)
                            cache_t = obj.get("cache_read_tokens", 0)
                            
                            input_tokens += in_t
                            output_tokens += out_t
                            cache_read_tokens += cache_t
                            
                            step_cost = calculate_step_cost_usd(current_model, in_t, cache_t, out_t)
                            session_cost_usd += step_cost
                            
                            if current_model not in per_model_usage:
                                per_model_usage[current_model] = {
                                    "turns": 0, "input_tokens": 0, "cache_read_tokens": 0, "output_tokens": 0, "cost_usd": 0.0
                                }
                            per_model_usage[current_model]["turns"] += 1
                            per_model_usage[current_model]["input_tokens"] += in_t
                            per_model_usage[current_model]["cache_read_tokens"] += cache_t
                            per_model_usage[current_model]["output_tokens"] += out_t
                            per_model_usage[current_model]["cost_usd"] += step_cost
                            
                            th = obj.get("thinking", "")
                            if th:
                                thinking_chars += len(th)
                                thinking_steps += 1
                                
                            if obj.get("content"):
                                model_responses_count += 1
                            tcalls = obj.get("tool_calls") or []
                            for tc in tcalls:
                                tname = tc.get("name") or tc.get("function", {}).get("name") or "unknown"
                                tools_by_name[tname] = tools_by_name.get(tname, 0) + 1
                                
                        elif stype == "GENERIC":
                            if "Command exited with code 1" in content or "Exit code 1" in content or \
                               ("error" in content.lower() and "exit code 0" not in content and "0 error(s)" not in content):
                                tool_errors_count += 1
                        elif stype == "ERROR_MESSAGE":
                            tool_errors_count += 1
                    except Exception:
                        pass

        is_subagent = (user_prompts_count == 0)
        if not is_subagent:
            parent_id = None
            prev_session_model = current_model  # update for next session continuity
            
        # Determine primary model (model with highest turns)
        if per_model_usage:
            primary_model = max(per_model_usage.items(), key=lambda x: x[1]["turns"])[0]
        else:
            primary_model = current_model

        # Discover artifacts
        artifacts = []
        if os.path.isdir(b_path):
            for item in os.listdir(b_path):
                ip = os.path.join(b_path, item)
                if os.path.isfile(ip) and item.endswith(".md"):
                    artifacts.append((item, ip))

        duration_seconds = 0
        if first_time and last_time:
            try:
                t0 = datetime.strptime(first_time[:19], "%Y-%m-%dT%H:%M:%S")
                t1 = datetime.strptime(last_time[:19], "%Y-%m-%dT%H:%M:%S")
                duration_seconds = max(0, int((t1 - t0).total_seconds()))
            except Exception:
                pass

        active_duration_seconds = 0.0
        model_active_sec = {}
        for i in range(len(step_timestamps) - 1):
            try:
                t0 = datetime.strptime(step_timestamps[i][0][:19], "%Y-%m-%dT%H:%M:%S")
                t1 = datetime.strptime(step_timestamps[i+1][0][:19], "%Y-%m-%dT%H:%M:%S")
                gap = (t1 - t0).total_seconds()
                if 0 <= gap <= 600:
                    active_duration_seconds += gap
                    m_mod = step_timestamps[i][1]
                    model_active_sec[m_mod] = model_active_sec.get(m_mod, 0.0) + gap
            except Exception:
                pass
        if active_duration_seconds == 0 and duration_seconds > 0:
            active_duration_seconds = min(duration_seconds, 600)

        total_tool_calls = sum(tools_by_name.values())

        sessions.append({
            "cid": cid,
            "start_time": first_time or "1970-01-01T00:00:00Z",
            "end_time": last_time or first_time or "1970-01-01T00:00:00Z",
            "duration_seconds": duration_seconds,
            "active_duration_seconds": int(round(active_duration_seconds)),
            "is_subagent": is_subagent,
            "parent_id": parent_id or step_0_sender,
            "first_prompt": first_prompt_clean,
            "primary_model": primary_model,
            "model_transitions": model_transitions,
            "per_model_usage": {
                k: {
                    "turns": v["turns"],
                    "active_seconds": int(round(model_active_sec.get(k, 0.0))),
                    "input_tokens": v["input_tokens"],
                    "cache_read_tokens": v["cache_read_tokens"],
                    "output_tokens": v["output_tokens"],
                    "cost_usd": round(v["cost_usd"], 4)
                } for k, v in per_model_usage.items()
            },
            "tokens": {
                "input_tokens": input_tokens,
                "cache_read_tokens": cache_read_tokens,
                "output_tokens": output_tokens,
                "total_tokens": input_tokens + cache_read_tokens + output_tokens
            },
            "thinking": {
                "character_count": thinking_chars,
                "steps_count": thinking_steps
            },
            "tools": {
                "total_calls": total_tool_calls,
                "by_name": tools_by_name,
                "error_calls": tool_errors_count
            },
            "compactions_count": compactions_count,
            "user_prompts_count": user_prompts_count,
            "model_responses_count": model_responses_count,
            "total_steps": total_steps,
            "estimated_cost_usd": round(session_cost_usd, 4),
            "artifacts": artifacts,
            "transcript_path": t_path
        })

    return sessions

def render_conversation_markdown(session, session_idx, folder_name):
    cid = session["cid"]
    t_path = session["transcript_path"]
    start_time = session["start_time"]
    end_time = session["end_time"]
    dur_min = session["duration_seconds"] // 60
    dur_sec = session["duration_seconds"] % 60
    is_subagent = session["is_subagent"]
    parent_id = session["parent_id"]
    artifacts = session["artifacts"]
    tok = session["tokens"]
    primary_model = session["primary_model"]
    cost = session["estimated_cost_usd"]
    per_model = session["per_model_usage"]

    title = session["first_prompt"].replace("\r", " ").replace("\n", " ")[:90] or "Subagent Execution"
    
    doc = []
    doc.append(f"# Session {session_idx:02d}: {title}\n")
    doc.append(f"- **Session ID:** `{cid}`")
    doc.append(f"- **Start Time:** {start_time}")
    doc.append(f"- **End Time:** {end_time} ({dur_min}m {dur_sec}s)")
    doc.append(f"- **Role:** {'Subagent' if is_subagent else 'Main Session'}")
    if is_subagent and parent_id:
        doc.append(f"- **Parent Session:** `{parent_id}`")
    doc.append(f"- **Primary Model:** `{primary_model}`")
    if len(per_model) > 1:
        model_breakdown_str = ", ".join([f"`{m}` ({v['turns']} turns, ${v['cost_usd']:.2f})" for m, v in per_model.items()])
        doc.append(f"- **Multi-Model Breakdown:** {model_breakdown_str}")
    doc.append(f"- **Tokens:** {tok['input_tokens']:,} input, {tok['cache_read_tokens']:,} cached, {tok['output_tokens']:,} output")
    doc.append(f"- **Estimated Cost:** ${cost:0.4f} USD")
    doc.append(f"- **Turn Stats:** {session['user_prompts_count']} user prompt(s), {session['model_responses_count']} assistant response(s), {session['tools']['total_calls']} tool call(s) ({session['tools']['error_calls']} error(s))")
    if session["compactions_count"] > 0:
        doc.append(f"- **Context Compactions:** {session['compactions_count']}")
    doc.append(f"- **Structured Metadata:** [session_metadata.json](session_metadata.json)")
    
    if artifacts:
        doc.append("\n### 📄 Generated Markdown Artifacts")
        for art_name, _ in artifacts:
            doc.append(f"- [{art_name}](artifacts/{art_name})")
    doc.append("\n---\n")

    if not os.path.exists(t_path):
        doc.append("*No transcript file found.*")
        return "\n".join(doc)

    with open(t_path, "r", encoding="utf-8", errors="replace") as f:
        steps = [json.loads(line) for line in f if line.strip()]

    i = 0
    while i < len(steps):
        step = steps[i]
        stype = step.get("type")
        source = step.get("source")
        created_at = step.get("created_at", "")
        content = step.get("content", "")

        if stype == "USER_INPUT":
            body, meta = clean_user_prompt(content)
            doc.append(f"## 👤 User Prompt ({created_at})\n")
            doc.append(body + "\n")
            if meta:
                doc.append(f"<details><summary>Additional Metadata</summary>\n\n```\n{meta}\n```\n</details>\n")
            doc.append("---\n")
            i += 1

        elif stype == "SYSTEM_MESSAGE":
            body, _ = clean_user_prompt(content)
            sender_info = f" (from `{parent_id}`)" if parent_id else ""
            doc.append(f"## ⚙️ System / Subagent Assignment{sender_info} ({created_at})\n")
            doc.append(f"> {body}\n")
            doc.append("---\n")
            i += 1

        elif stype == "CHECKPOINT":
            doc.append(f"## 📌 Checkpoint / Compaction ({created_at})\n")
            doc.append(f"<details><summary>Compaction Summary</summary>\n\n{content}\n\n</details>\n")
            doc.append("---\n")
            i += 1

        elif stype == "ERROR_MESSAGE":
            doc.append(f"## ⚠️ Error ({created_at})\n")
            doc.append(f"```\n{content}\n```\n")
            doc.append("---\n")
            i += 1

        elif stype == "PLANNER_RESPONSE":
            doc.append(f"## 🤖 Assistant ({created_at})\n")
            thinking = step.get("thinking")
            if thinking:
                doc.append(f"<details>\n<summary>💭 Thought Process ({len(thinking):,} chars)</summary>\n\n{thinking.strip()}\n\n</details>\n")
                
            tool_calls = step.get("tool_calls") or []
            if tool_calls:
                for tc in tool_calls:
                    tname, tsummary, tdetails = format_tool_call(tc)
                    header_tag = f"**🔧 Action:** `{tname}`"
                    if tsummary and tsummary != tname:
                        header_tag += f" ({tsummary})"
                    doc.append(f"{header_tag}\n\n{tdetails}\n")
                    
                    if i + 1 < len(steps) and steps[i+1].get("type") == "GENERIC":
                        i += 1
                        out_content = steps[i].get("content", "")
                        doc.append(format_tool_output(out_content))

            model_text = step.get("content")
            if model_text:
                doc.append(f"\n### Assistant Response\n\n{model_text.strip()}\n")
            doc.append("---\n")
            i += 1

        elif stype == "GENERIC":
            doc.append(format_tool_output(content))
            i += 1
        else:
            i += 1

    return "\n".join(doc)

def main():
    print(f"Scanning Antigravity conversations and brain directories...")
    sessions = collect_vson_demand_sessions()
    print(f"Found {len(sessions)} sessions belonging to VSOnDemandSymbols.")

    os.makedirs(OUTPUT_DIR, exist_ok=True)
    os.makedirs(CENTRAL_ARTIFACTS_DIR, exist_ok=True)

    # Pre-pass: identify any additional meta sessions by prompt and propagate to subagents
    meta_ids = set(META_SESSION_IDS)
    for s in sessions:
        if classify_session_category(s["cid"], s["parent_id"], s["first_prompt"], meta_ids) == "meta":
            meta_ids.add(s["cid"])

    dev_index_rows = []
    meta_index_rows = []
    total_artifacts = 0
    total_in_tok = 0
    total_cache_tok = 0
    total_out_tok = 0
    total_cost_usd = 0.0
    global_model_summary = {}

    def empty_cat_bucket():
        return {
            "total_sessions": 0,
            "total_main_sessions": 0,
            "total_subagent_sessions": 0,
            "turns": 0,
            "input_tokens": 0,
            "cache_read_tokens": 0,
            "output_tokens": 0,
            "grand_total_tokens": 0,
            "total_tool_calls": 0,
            "total_tool_errors": 0,
            "compactions_count": 0,
            "estimated_cost_usd": 0.0,
            "models_summary": {},
        }

    category_summary = {
        "development": empty_cat_bucket(),
        "meta": empty_cat_bucket(),
    }

    summary_sessions_list = []

    for idx, s in enumerate(sessions, 1):
        cid = s["cid"]
        cid_short = cid[:8]
        date_str = s["start_time"][:10]
        slug = make_slug(s["first_prompt"] or ("subagent" if s["is_subagent"] else "session"))
        folder_name = f"{idx:02d}_{date_str}_{cid_short}_{slug}"
        session_dir = os.path.join(OUTPUT_DIR, folder_name)
        os.makedirs(session_dir, exist_ok=True)

        category = classify_session_category(cid, s["parent_id"], s["first_prompt"], meta_ids)
        cat_bucket = category_summary[category]

        tok = s["tokens"]
        total_in_tok += tok["input_tokens"]
        total_cache_tok += tok["cache_read_tokens"]
        total_out_tok += tok["output_tokens"]
        total_cost_usd += s["estimated_cost_usd"]

        cat_bucket["total_sessions"] += 1
        if s["is_subagent"]:
            cat_bucket["total_subagent_sessions"] += 1
        else:
            cat_bucket["total_main_sessions"] += 1
        cat_bucket["input_tokens"] += tok["input_tokens"]
        cat_bucket["cache_read_tokens"] += tok["cache_read_tokens"]
        cat_bucket["output_tokens"] += tok["output_tokens"]
        cat_bucket["grand_total_tokens"] += tok["total_tokens"]
        cat_bucket["total_tool_calls"] += s["tools"]["total_calls"]
        cat_bucket["total_tool_errors"] += s["tools"]["error_calls"]
        cat_bucket["compactions_count"] += s["compactions_count"]
        cat_bucket["estimated_cost_usd"] += s["estimated_cost_usd"]
        cat_bucket["active_time_seconds"] = cat_bucket.get("active_time_seconds", 0) + s.get("active_duration_seconds", 0)

        for m_name, m_stats in s["per_model_usage"].items():
            for summary_dict in (global_model_summary, cat_bucket["models_summary"]):
                if m_name not in summary_dict:
                    summary_dict[m_name] = {
                        "sessions": 0, "turns": 0, "active_seconds": 0, "input_tokens": 0, "cache_read_tokens": 0, "output_tokens": 0, "cost_usd": 0.0
                    }
                summary_dict[m_name]["turns"] += m_stats["turns"]
                summary_dict[m_name]["active_seconds"] = summary_dict[m_name].get("active_seconds", 0) + m_stats.get("active_seconds", 0)
                summary_dict[m_name]["input_tokens"] += m_stats["input_tokens"]
                summary_dict[m_name]["cache_read_tokens"] += m_stats["cache_read_tokens"]
                summary_dict[m_name]["output_tokens"] += m_stats["output_tokens"]
                summary_dict[m_name]["cost_usd"] += m_stats["cost_usd"]
            cat_bucket["turns"] += m_stats["turns"]

        # Track session count per model
        for m_name in s["per_model_usage"].keys():
            global_model_summary[m_name]["sessions"] += 1
            cat_bucket["models_summary"][m_name]["sessions"] += 1

        # Copy artifacts
        copied_artifacts = []
        if s["artifacts"]:
            session_artifacts_dir = os.path.join(session_dir, "artifacts")
            os.makedirs(session_artifacts_dir, exist_ok=True)
            for art_name, art_path in s["artifacts"]:
                dest_local = os.path.join(session_artifacts_dir, art_name)
                shutil.copy2(art_path, dest_local)
                dest_central = os.path.join(CENTRAL_ARTIFACTS_DIR, f"{cid_short}_{art_name}")
                shutil.copy2(art_path, dest_central)
                copied_artifacts.append(art_name)
                total_artifacts += 1

        # Write session_metadata.json
        meta_dict = {
            "session_index": idx,
            "session_id": cid,
            "category": category,
            "folder_name": folder_name,
            "start_time": s["start_time"],
            "end_time": s["end_time"],
            "duration_seconds": s["duration_seconds"],
            "active_duration_seconds": s["active_duration_seconds"],
            "role": "Subagent" if s["is_subagent"] else "Main Session",
            "parent_id": s["parent_id"],
            "primary_model": s["primary_model"],
            "per_model_usage": s["per_model_usage"],
            "model_transitions": s["model_transitions"],
            "summary_prompt": s["first_prompt"][:250],
            "tokens": tok,
            "thinking": s["thinking"],
            "tools": s["tools"],
            "compactions_count": s["compactions_count"],
            "user_prompts_count": s["user_prompts_count"],
            "model_responses_count": s["model_responses_count"],
            "total_steps": s["total_steps"],
            "estimated_cost_usd": s["estimated_cost_usd"],
            "artifacts": copied_artifacts
        }
        
        with open(os.path.join(session_dir, "session_metadata.json"), "w", encoding="utf-8") as f:
            json.dump(meta_dict, f, indent=2)

        summary_sessions_list.append(meta_dict)

        # Render conversation markdown
        conv_md = render_conversation_markdown(s, idx, folder_name)
        conv_path = os.path.join(session_dir, "conversation.md")
        with open(conv_path, "w", encoding="utf-8") as f:
            f.write(conv_md)

        # Index entry
        summary_clean = s["first_prompt"].replace("\r", " ").replace("\n", " ")[:60]
        if not summary_clean:
            summary_clean = f"Subagent task (parent: {s['parent_id'][:8] if s['parent_id'] else 'unknown'})"
        summary_clean = summary_clean.replace("|", "\\|")
        
        art_links = ", ".join([f"[{a}]({folder_name}/artifacts/{a})" for a in copied_artifacts]) or "-"
        role_label = "Subagent" if s["is_subagent"] else "Main"
        
        tok_str = f"{tok['input_tokens']/1000:0.1f}k / {tok['cache_read_tokens']/1000000:0.1f}M / {tok['output_tokens']/1000:0.1f}k"
        tool_err_str = f"{s['tools']['total_calls']} ({s['tools']['error_calls']})"
        comp_str = str(s['compactions_count']) if s['compactions_count'] > 0 else "-"
        
        # Display multi-model indicator if applicable
        model_display = f"`{s['primary_model']}`"
        if len(s["per_model_usage"]) > 1:
            model_display += f" *(+{len(s['per_model_usage'])-1})*"

        row_str = f"| {idx:02d} | [{folder_name}]({folder_name}/conversation.md) | {s['start_time'][:16]} | {role_label} | {model_display} | {tok_str} | ${s['estimated_cost_usd']:0.2f} | {tool_err_str} | {comp_str} | {art_links} | {summary_clean} |"
        if category == "meta":
            meta_index_rows.append(row_str)
        else:
            dev_index_rows.append(row_str)
        print(f"Processed [{idx:02d}/{len(sessions):02d}] [{category}] {folder_name} (Primary: {s['primary_model']}, Exact Cost: ${s['estimated_cost_usd']:0.2f})")

    # Round global and category summaries
    for m in global_model_summary.values():
        m["cost_usd"] = round(m["cost_usd"], 2)
    for cat_bucket in category_summary.values():
        cat_bucket["estimated_cost_usd"] = round(cat_bucket["estimated_cost_usd"], 2)
        cat_bucket["cache_hit_ratio_percent"] = round(
            cat_bucket["cache_read_tokens"] * 100.0 / max(1, cat_bucket["input_tokens"] + cat_bucket["cache_read_tokens"]), 2
        )
        for m in cat_bucket["models_summary"].values():
            m["cost_usd"] = round(m["cost_usd"], 2)

    master_summary = {
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "dataset": "VSOnDemandSymbols",
        "total_sessions": len(sessions),
        "total_main_sessions": sum(1 for s in sessions if not s["is_subagent"]),
        "total_subagent_sessions": sum(1 for s in sessions if s["is_subagent"]),
        "total_artifacts": total_artifacts,
        "token_totals": {
            "input_tokens": total_in_tok,
            "cache_read_tokens": total_cache_tok,
            "output_tokens": total_out_tok,
            "grand_total_tokens": total_in_tok + total_cache_tok + total_out_tok,
            "cache_hit_ratio_percent": round(total_cache_tok * 100.0 / max(1, total_in_tok + total_cache_tok), 2)
        },
        "total_estimated_cost_usd": round(total_cost_usd, 2),
        "category_summary": category_summary,
        "models_summary": global_model_summary,
        "sessions": summary_sessions_list
    }
    
    summary_path = os.path.join(OUTPUT_DIR, "summary_metrics.json")
    with open(summary_path, "w", encoding="utf-8") as f:
        json.dump(master_summary, f, indent=2)

    dev_sum = category_summary["development"]
    meta_sum = category_summary["meta"]

    # Write INDEX.md
    index_md = []
    index_md.append("# VSOnDemandSymbols - Complete Antigravity Chat & Development History\n")
    index_md.append(f"This directory contains the complete self-contained extracted conversation history, user prompts, assistant reasoning, tool actions, design artifacts, and exact step-level financial metadata generated while building the **VSOnDemandSymbols** Visual Studio extension with Antigravity.\n")
    index_md.append("## Overview Statistics (Split: Development vs. Meta)\n")
    index_md.append("| Scope | Sessions (Main / Sub) | Turns | Input Tokens | Cached Tokens | Output Tokens | Cache Hit | Tools (Errors) | Exact Cost (USD) |")
    index_md.append("|---|---|---|---|---|---|---|---|---|")
    index_md.append(
        f"| **Extension Development** | **{dev_sum['total_sessions']}** ({dev_sum['total_main_sessions']} / {dev_sum['total_subagent_sessions']}) | {dev_sum['turns']:,} | {dev_sum['input_tokens']:,} | {dev_sum['cache_read_tokens']:,} | {dev_sum['output_tokens']:,} | {dev_sum['cache_hit_ratio_percent']}% | {dev_sum['total_tool_calls']:,} ({dev_sum['total_tool_errors']:,}) | **${dev_sum['estimated_cost_usd']:0.2f}** |"
    )
    index_md.append(
        f"| **Meta (Analysis & Slide Deck)** | **{meta_sum['total_sessions']}** ({meta_sum['total_main_sessions']} / {meta_sum['total_subagent_sessions']}) | {meta_sum['turns']:,} | {meta_sum['input_tokens']:,} | {meta_sum['cache_read_tokens']:,} | {meta_sum['output_tokens']:,} | {meta_sum['cache_hit_ratio_percent']}% | {meta_sum['total_tool_calls']:,} ({meta_sum['total_tool_errors']:,}) | **${meta_sum['estimated_cost_usd']:0.2f}** |"
    )
    index_md.append(
        f"| **Combined Total** | **{len(sessions)}** ({master_summary['total_main_sessions']} / {master_summary['total_subagent_sessions']}) | {dev_sum['turns'] + meta_sum['turns']:,} | {total_in_tok:,} | {total_cache_tok:,} | {total_out_tok:,} | {master_summary['token_totals']['cache_hit_ratio_percent']}% | {dev_sum['total_tool_calls'] + meta_sum['total_tool_calls']:,} ({dev_sum['total_tool_errors'] + meta_sum['total_tool_errors']:,}) | **${total_cost_usd:0.2f}** |"
    )
    index_md.append("")
    index_md.append(f"- **Date Range:** {sessions[0]['start_time'][:10]} to {sessions[-1]['start_time'][:10]}")
    index_md.append(f"- **Total Markdown Artifacts:** {total_artifacts}")
    index_md.append(f"- **Central Artifacts Directory:** [artifacts/](artifacts/)")
    index_md.append(f"- **Complete Metrics Dataset (JSON):** [summary_metrics.json](summary_metrics.json)")
    index_md.append(f"- **Analysis Plan & Findings:** [ANALYSIS_PLAN.md](ANALYSIS_PLAN.md)")
    index_md.append(f"- **Session 10 Forensic Deep Dive:** [SESSION_10_DEEP_DIVE.md](SESSION_10_DEEP_DIVE.md)")
    index_md.append(f"- **Session 51 Forensic Deep Dive:** [SESSION_51_DEEP_DIVE.md](SESSION_51_DEEP_DIVE.md)\n")

    index_md.append("## Model Usage Breakdown — Extension Development\n")
    index_md.append("| Model | Active Sessions | Total Turns | Input Tokens | Cached Tokens | Output Tokens | Exact Cost (USD) |")
    index_md.append("|---|---|---|---|---|---|---|")
    for mod_name, mod_data in sorted(dev_sum["models_summary"].items(), key=lambda x: x[1]["cost_usd"], reverse=True):
        index_md.append(
            f"| `{mod_name}` | {mod_data['sessions']} | {mod_data['turns']:,} | {mod_data['input_tokens']:,} | {mod_data['cache_read_tokens']:,} | {mod_data['output_tokens']:,} | ${mod_data['cost_usd']:0.2f} |"
        )
    index_md.append("\n## Model Usage Breakdown — Meta (Analysis & Slide Deck)\n")
    index_md.append("| Model | Active Sessions | Total Turns | Input Tokens | Cached Tokens | Output Tokens | Exact Cost (USD) |")
    index_md.append("|---|---|---|---|---|---|---|")
    for mod_name, mod_data in sorted(meta_sum["models_summary"].items(), key=lambda x: x[1]["cost_usd"], reverse=True):
        index_md.append(
            f"| `{mod_name}` | {mod_data['sessions']} | {mod_data['turns']:,} | {mod_data['input_tokens']:,} | {mod_data['cache_read_tokens']:,} | {mod_data['output_tokens']:,} | ${mod_data['cost_usd']:0.2f} |"
        )
    index_md.append("\n---\n")

    index_md.append("## Development Sessions Index\n")
    index_md.append("| # | Session Link | Date / Time (UTC) | Role | Primary Model | Tokens (In / Cache / Out) | Cost | Tools (Err) | Comp. | Artifacts | Topic / Prompt |")
    index_md.append("|---|---|---|---|---|---|---|---|---|---|---|")
    index_md.extend(dev_index_rows)
    index_md.append("\n---\n")

    index_md.append("## Meta Sessions Index (Analysis & Slide Deck)\n")
    index_md.append("| # | Session Link | Date / Time (UTC) | Role | Primary Model | Tokens (In / Cache / Out) | Cost | Tools (Err) | Comp. | Artifacts | Topic / Prompt |")
    index_md.append("|---|---|---|---|---|---|---|---|---|---|---|")
    index_md.extend(meta_index_rows)
    index_md.append("\n---\n")
    index_md.append("## How This History Was Extracted\n")
    index_md.append("The conversations were parsed from Antigravity's internal conversation storage (`C:\\Users\\matt\\.gemini\\antigravity\\conversations\\*.db` and `C:\\Users\\matt\\.gemini\\antigravity\\brain\\<uuid>\\`).")
    index_md.append("All transcripts, token counts, models, and metadata are completely self-contained in this `chat_history/` directory. No external `.gemini` paths are required for analysis.")
    index_md.append("To refresh or re-run this extraction at any time, run:")
    index_md.append("```powershell\npython chat_history/extract_chat_history.py\n```\n")

    index_path = os.path.join(OUTPUT_DIR, "INDEX.md")
    with open(index_path, "w", encoding="utf-8") as f:
        f.write("\n".join(index_md))

    readme_path = os.path.join(OUTPUT_DIR, "README.md")
    with open(readme_path, "w", encoding="utf-8") as f:
        f.write("\n".join(index_md))

    print(f"\nExtraction complete! Saved {len(sessions)} sessions, {total_artifacts} artifacts, and summary_metrics.json to {OUTPUT_DIR}.")
    print(f"Master index written to {index_path}.")

if __name__ == "__main__":
    main()
