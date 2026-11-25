namespace Meloberry.Infrastructure.Web.Components.ContentSpecs;

internal static class QuestionSpecsSet2
{
    private const string RecallUtilsJs = """
(function (MBY) {
  const GREEK_CHAR_MAP = {
    'α': 'alpha', 'β': 'beta', 'γ': 'gamma', 'δ': 'delta', 'ε': 'epsilon', 'ϵ': 'epsilon',
    'ζ': 'zeta', 'η': 'eta', 'θ': 'theta', 'ϑ': 'theta', 'ι': 'iota', 'κ': 'kappa',
    'λ': 'lambda', 'μ': 'mu', 'µ': 'mu', 'ν': 'nu', 'ξ': 'xi', 'ο': 'omicron', 'π': 'pi',
    'ρ': 'rho', 'σ': 'sigma', 'ς': 'sigma', 'τ': 'tau', 'υ': 'upsilon', 'φ': 'phi', 'ϕ': 'phi',
    'χ': 'chi', 'ψ': 'psi', 'ω': 'omega',
    'Α': 'Alpha', 'Β': 'Beta', 'Γ': 'Gamma', 'Δ': 'Delta', 'Ε': 'Epsilon',
    'Ζ': 'Zeta', 'Η': 'Eta', 'Θ': 'Theta', 'Ι': 'Iota', 'Κ': 'Kappa',
    'Λ': 'Lambda', 'Μ': 'Mu', 'Ν': 'Nu', 'Ξ': 'Xi', 'Ο': 'Omicron', 'Π': 'Pi',
    'Ρ': 'Rho', 'Σ': 'Sigma', 'Τ': 'Tau', 'Υ': 'Upsilon', 'Φ': 'Phi',
    'Χ': 'Chi', 'Ψ': 'Psi', 'Ω': 'Omega'
  };
  const GREEK_CHAR_RE = /[αβεϵζηθϑικλμµνξοπρσςτυφϕχψωΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠΡΣΤΥΦΧΨΩ]/g;

  function normalizeRecallAnswer(raw, caseSensitive) {
    let s = String(raw ?? '').trim().replace(/\s+/g, ' ');
    if (!caseSensitive) s = s.toLowerCase();
    s = s.replace(GREEK_CHAR_RE, ch => {
      const name = GREEK_CHAR_MAP[ch] || ch;
      return caseSensitive ? name : name.toLowerCase();
    });
    s = s.replace(/[‐‑‒–—−]/g, '-').replace(/[,.;:()]/g, ' ').replace(/\s+/g, ' ').trim();
    return s;
  }

  function levenshtein(a, b) {
    const m = a.length, n = b.length;
    const dp = Array.from({ length: m + 1 }, () => new Array(n + 1).fill(0));
    for (let i = 0; i <= m; i++) dp[i][0] = i;
    for (let j = 0; j <= n; j++) dp[0][j] = j;
    for (let i = 1; i <= m; i++) {
      for (let j = 1; j <= n; j++) {
        dp[i][j] = Math.min(dp[i - 1][j] + 1, dp[i][j - 1] + 1, dp[i - 1][j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
      }
    }
    return dp[m][n];
  }

  function evaluateRecallMatch(userInput, acceptedAnswers, fuzzy, caseSensitive) {
    const uNorm = normalizeRecallAnswer(userInput, caseSensitive);
    if (!uNorm) return 'wrong';
    const uCompact = uNorm.replace(/[-\s]/g, '');
    for (const ans of (acceptedAnswers || [])) {
      const aNorm = normalizeRecallAnswer(ans, caseSensitive);
      if (uNorm === aNorm || uCompact === aNorm.replace(/[-\s]/g, '')) return 'exact';
    }
    if (fuzzy) {
      for (const ans of (acceptedAnswers || [])) {
        const aNorm = normalizeRecallAnswer(ans, caseSensitive);
        if (aNorm.length >= 6 && levenshtein(uNorm, aNorm) <= 1) return 'fuzzy';
        const aCompact = aNorm.replace(/[-\s]/g, '');
        if (aCompact.length >= 6 && levenshtein(uCompact, aCompact) <= 1) return 'fuzzy';
      }
    }
    return 'wrong';
  }

  MBY.helpers.normalizeRecallAnswer = normalizeRecallAnswer;
  MBY.helpers.levenshtein = levenshtein;
  MBY.helpers.evaluateRecallMatch = evaluateRecallMatch;
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    private const string BlankInputCss = """
.mby-blank-wrap { display: inline-flex; flex-direction: column; vertical-align: baseline; margin: 0 3px; }
.mby-blank-input { font: inherit; font-size: 0.92em; padding: 3px 10px; min-width: 110px; max-width: 240px; border-radius: 8px; border: 2px solid var(--border); background: var(--bg); color: var(--text); outline: none; transition: border-color 140ms ease, background 140ms ease; }
.mby-blank-input:focus { border-color: var(--select); background: var(--surface); }
.mby-blank-input.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.12); }
.mby-blank-input.fuzzy-ok { border-color: var(--callout-pearl); background: var(--callout-pearl-bg); }
.mby-blank-input.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.12); }
.mby-blank-expected { font-size: 12px; font-weight: 700; color: var(--ok); margin-top: 2px; line-height: 1.3; }
""";

    private const string NoErrorCss = """
.mby-no-error-btn { display: inline-flex; align-items: center; justify-content: center; padding: 10px 16px; border-radius: 10px; border: 1.5px solid var(--border); background: var(--bg); color: var(--text); font-weight: 600; font-size: 14px; cursor: pointer; transition: border-color 140ms ease, background 140ms ease, color 140ms ease; }
.mby-no-error-btn:hover { border-color: var(--select); }
.mby-no-error-btn.selected { border-color: var(--select); background: var(--select-soft); color: var(--select); }
.mby-no-error-btn.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.14); color: var(--ok); }
.mby-no-error-btn.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.14); color: var(--bad); }
""";

    private const string FillBlankCss = """
.mby-blank-prompt { font-size: var(--prompt-font-size, 22px); font-weight: 550; line-height: 1.7; margin-bottom: 16px; }
""";

    private const string FillBlankJs = """
(function (MBY) {
  function buildFillBlankCard(slide, ctx, onAnswered) {
    const m = ctx.metricById.get(slide.id), fb = slide.fillBlank;
    const card = mk('div', 'mby-card', null, [createUpperTab('FILL IN THE BLANK')]);
    applySlideLayoutVars(card, resolveSlideLayout(slide, 'mcq'));
    if (!ctx.gradedStateMap.has(slide.id)) ctx.gradedStateMap.set(slide.id, { values: [], checked: false, isCorrect: null, overridden: false });
    const st = ctx.gradedStateMap.get(slide.id);

    let html = fb.promptHtml || '';
    const placeholderAttr = fb.placeholder ? ` placeholder="${escapeHtml(fb.placeholder)}"` : '';
    (fb.blanks || []).forEach((b, idx) => {
      html = html.replace('MBYBLANKSLOT' + idx + 'END', `<span class="mby-blank-wrap" data-slot="${idx}"><input type="text" class="mby-blank-input" data-slot="${idx}" autocomplete="off" spellcheck="false"${placeholderAttr} /><span class="mby-blank-expected hidden" data-expected="${idx}"></span></span>`);
    });

    const promptEl = mk('div', 'mby-blank-prompt');
    promptEl.innerHTML = html;
    ctx.renderMathIn(promptEl);

    const inputs = Array.from(promptEl.querySelectorAll('.mby-blank-input'));
    const expectedEls = Array.from(promptEl.querySelectorAll('.mby-blank-expected'));
    inputs.forEach((inp, idx) => {
      if (st.values[idx]) inp.value = st.values[idx];
      inp.addEventListener('input', () => { st.values[idx] = inp.value; });
      inp.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); checkBtn.click(); } });
    });

    const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
    const overrideBtn = mk('button', 'mby-override-btn hidden', 'Override: Mark Correct'); overrideBtn.type = 'button';
    const feedback = mk('span');
    const explanationBox = MBY.helpers.createExplanationBox(fb.explanationHtml, st.checked, ctx);
    const retryBtn = MBY.helpers.createRetryBtn(() => {
      st.values = [];
      st.checked = false;
      st.isCorrect = null;
      st.overridden = false;
      inputs.forEach((inp, idx) => {
        inp.value = '';
        inp.classList.remove('correct', 'fuzzy-ok', 'wrong');
        if (expectedEls[idx]) { expectedEls[idx].innerHTML = ''; expectedEls[idx].classList.add('hidden'); }
      });
      feedback.textContent = '';
      overrideBtn.classList.add('hidden');
      if (explanationBox) explanationBox.classList.add('hidden');
      retryBtn.classList.add('hidden');
      if (m) m.isCorrect = null;
      if (onAnswered) onAnswered();
      requestAnimationFrame(ctx.updateScrollDownHint);
    }, st.checked);

    function applyGradeUi(allOk, anyFuzzy) {
      MBY.helpers.setFeedbackMessage(feedback, allOk ? (anyFuzzy ? 'Almost Correct!' : 'Correct!') : 'Review highlighted blanks.', allOk);
      overrideBtn.classList.toggle('hidden', allOk);
      retryBtn.classList.remove('hidden');
      if (explanationBox) explanationBox.classList.remove('hidden');
    }

    checkBtn.addEventListener('click', () => {
      let allOk = true, anyFuzzy = false;
      inputs.forEach((inp, idx) => {
        st.values[idx] = inp.value;
        const slot = fb.blanks[idx];
        const verdict = MBY.helpers.evaluateRecallMatch(inp.value, slot?.answers || [], fb.fuzzy, fb.caseSensitive);
        inp.classList.remove('correct', 'fuzzy-ok', 'wrong');
        if (verdict === 'exact') { inp.classList.add('correct'); expectedEls[idx]?.classList.add('hidden'); }
        else if (verdict === 'fuzzy') { inp.classList.add('fuzzy-ok'); anyFuzzy = true; if (expectedEls[idx]) { expectedEls[idx].innerHTML = 'Accepted: ' + (slot?.primaryAnswerHtml || ''); ctx.renderMathIn(expectedEls[idx]); expectedEls[idx].classList.remove('hidden'); } }
        else { inp.classList.add('wrong'); allOk = false; if (expectedEls[idx]) { expectedEls[idx].innerHTML = 'Answer: ' + (slot?.primaryAnswerHtml || ''); ctx.renderMathIn(expectedEls[idx]); expectedEls[idx].classList.remove('hidden'); } }
      });
      st.checked = true; st.isCorrect = allOk; st.overridden = false;
      MBY.helpers.recordGradeAttempt(m, allOk);
      applyGradeUi(allOk, anyFuzzy);
      if (onAnswered) onAnswered();
      requestAnimationFrame(ctx.updateScrollDownHint);
    });

    overrideBtn.addEventListener('click', () => {
      st.isCorrect = true; st.overridden = true;
      inputs.forEach(inp => { inp.classList.remove('wrong'); inp.classList.add('correct'); });
      if (m) m.isCorrect = true;
      MBY.helpers.setFeedbackMessage(feedback, 'Marked correct (self-override).', true);
      overrideBtn.classList.add('hidden');
      retryBtn.classList.remove('hidden');
      if (onAnswered) onAnswered();
    });

    card.appendChild(mk('div', 'mby-card-body', null, [promptEl, mk('div', 'mby-action-row', null, [checkBtn, overrideBtn, retryBtn, feedback]), explanationBox]));
    return card;
  }

  MBY.gradedKinds['fill-blank'] = true;
  MBY.kindLabels['fill-blank'] = 'Fill in the Blank';
  MBY.questions['fill-blank'] = buildFillBlankCard;
  MBY.search['fill-blank'] = (q, parts, ctx) => {
    if (!q.fillBlank) return;
    parts.push(ctx.htmlToPlainText(q.fillBlank.promptHtml).replace(/MBYBLANKSLOT\d+END/g, ' ___ '));
    (q.fillBlank.blanks || []).forEach(b => parts.push((b.answers || []).join(' ')));
    if (q.fillBlank.explanationHtml) parts.push(ctx.htmlToPlainText(q.fillBlank.explanationHtml));
  };
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    private const string SpotErrorCss = """
.mby-spot-statement { font-size: var(--prompt-font-size, 22px); font-weight: 500; line-height: 2.0; margin-bottom: 16px; }
.mby-error-chip-wrap { display: inline-flex; flex-direction: column; align-items: center; vertical-align: bottom; margin: 0 2px; }
.mby-error-chip { display: inline; padding: 1px 6px; margin: 0; border-radius: 6px; border: 1.5px solid var(--border); background: var(--bg); color: var(--text); font: inherit; font-size: 0.88em; line-height: 1.35; cursor: pointer; transition: border-color 140ms ease, background 140ms ease; }
.mby-error-chip:hover { border-color: var(--select); }
.mby-error-chip.selected { border-color: var(--select); background: var(--select-soft); color: var(--select); font-weight: 650; }
.mby-error-chip.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.14); color: var(--ok); }
.mby-error-chip.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.14); color: var(--bad); }
.mby-error-chip.missed-error { border-color: var(--bad); border-style: dashed; background: rgba(220, 53, 69, 0.08); }
.mby-error-fix-badge { display: inline-block; margin-bottom: 3px; padding: 1px 7px; border-radius: 6px; background: rgba(25, 135, 84, 0.16); color: var(--ok); font-size: 0.72em; font-weight: 700; line-height: 1.25; white-space: nowrap; }
.mby-fix-input-row { display: flex; flex-direction: column; gap: 8px; margin-bottom: 14px; padding: 12px 14px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); }
.mby-fix-input-label { font-size: 12px; font-weight: 700; color: var(--muted); }
.mby-fix-input { font: inherit; font-size: 15px; padding: 7px 11px; border-radius: 8px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); outline: none; }
.mby-fix-input:focus { border-color: var(--select); }
""";

    private const string SpotErrorJs = """
(function (MBY) {
  function buildSpotErrorCard(slide, ctx, onAnswered) {
    const m = ctx.metricById.get(slide.id), se = slide.spotError;
    const card = mk('div', 'mby-card', null, [createUpperTab('SPOT THE ERROR')]);
    applySlideLayoutVars(card, resolveSlideLayout(slide, 'mcq'));
    if (!ctx.gradedStateMap.has(slide.id)) ctx.gradedStateMap.set(slide.id, { selectedSpans: new Set(), pickedNoError: false, fixes: {}, checked: false, isCorrect: null });
    const st = ctx.gradedStateMap.get(slide.id);

    const promptMedia = se.imagePath ? mk('div', 'mby-prompt-media', null, [ctx.makeZoomImg(se.imagePath)]) : null;
    const stmtEl = mk('div', 'mby-spot-statement');
    const chipByIdx = new Map();
    const fixRow = mk('div', 'mby-fix-input-row hidden');
    const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';

    function rebuildFixInputs() {
      if (!se.requireFix) return;
      fixRow.innerHTML = '';
      if (st.pickedNoError || st.selectedSpans.size === 0) { fixRow.classList.add('hidden'); return; }
      fixRow.classList.remove('hidden');
      Array.from(st.selectedSpans).sort((a, b) => a - b).forEach(spanIdx => {
        const seg = se.segments.find(s => s.spanIndex === spanIdx);
        const label = mk('label', 'mby-fix-input-label', `Correction for "${ctx.htmlToPlainText(seg?.html || '')}":`);
        const inp = mk('input', 'mby-fix-input');
        inp.type = 'text'; inp.autocomplete = 'off'; inp.placeholder = 'Type the corrected wording...';
        if (st.fixes[spanIdx]) inp.value = st.fixes[spanIdx];
        inp.addEventListener('input', () => { st.fixes[spanIdx] = inp.value; });
        inp.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); checkBtn.click(); } });
        fixRow.appendChild(label); fixRow.appendChild(inp);
      });
    }

    let noErrorBtn = null;
    (se.segments || []).forEach(seg => {
      if (!seg.isCandidate) {
        const sp = mk('span'); sp.innerHTML = seg.html; ctx.renderMathIn(sp);
        stmtEl.appendChild(sp);
        return;
      }
      const chipWrap = mk('span', 'mby-error-chip-wrap');
      const chip = mk('button', 'mby-error-chip'); chip.type = 'button';
      chip.innerHTML = seg.html; ctx.renderMathIn(chip);
      if (st.selectedSpans.has(seg.spanIndex)) chip.classList.add('selected');
      chip.addEventListener('click', () => {
        st.pickedNoError = false;
        if (noErrorBtn) noErrorBtn.classList.remove('selected');
        if (st.selectedSpans.has(seg.spanIndex)) { st.selectedSpans.delete(seg.spanIndex); chip.classList.remove('selected'); }
        else { st.selectedSpans.add(seg.spanIndex); chip.classList.add('selected'); }
        rebuildFixInputs();
      });
      chipWrap.appendChild(chip);
      chipByIdx.set(seg.spanIndex, { chip, seg });
      stmtEl.appendChild(chipWrap);
    });

    if (se.allowNoError || se.noError) {
      noErrorBtn = mk('button', 'mby-no-error-btn' + (st.pickedNoError ? ' selected' : ''), 'No error in this statement');
      noErrorBtn.type = 'button';
      noErrorBtn.addEventListener('click', () => {
        st.pickedNoError = !st.pickedNoError;
        noErrorBtn.classList.toggle('selected', st.pickedNoError);
        if (st.pickedNoError) {
          st.selectedSpans.clear();
          chipByIdx.forEach(({ chip }) => chip.classList.remove('selected'));
        }
        rebuildFixInputs();
      });
    }

    rebuildFixInputs();
    const feedback = mk('span');
    const explanationBox = MBY.helpers.createExplanationBox(se.explanationHtml, st.checked, ctx);
    const retryBtn = MBY.helpers.createRetryBtn(() => {
      st.selectedSpans.clear();
      st.pickedNoError = false;
      st.fixes = {};
      st.checked = false;
      st.isCorrect = null;
      stmtEl.querySelectorAll('.mby-error-fix-badge').forEach(el => el.remove());
      chipByIdx.forEach(({ chip }) => chip.classList.remove('selected', 'correct', 'wrong', 'missed-error'));
      if (noErrorBtn) noErrorBtn.classList.remove('selected', 'correct', 'wrong');
      rebuildFixInputs();
      feedback.textContent = '';
      if (explanationBox) explanationBox.classList.add('hidden');
      retryBtn.classList.add('hidden');
      if (m) m.isCorrect = null;
      if (onAnswered) onAnswered();
      requestAnimationFrame(ctx.updateScrollDownHint);
    }, st.checked);

    checkBtn.addEventListener('click', () => {
      let allOk = true;
      stmtEl.querySelectorAll('.mby-error-fix-badge').forEach(el => el.remove());

      if (se.noError) {
        allOk = st.pickedNoError && st.selectedSpans.size === 0;
        if (noErrorBtn) { noErrorBtn.classList.remove('correct', 'wrong'); noErrorBtn.classList.add('correct'); }
        chipByIdx.forEach(({ chip, seg }) => {
          chip.classList.remove('correct', 'wrong', 'missed-error');
          if (st.selectedSpans.has(seg.spanIndex)) chip.classList.add('wrong');
        });
      } else {
        if (st.pickedNoError) { allOk = false; noErrorBtn?.classList.add('wrong'); }
        else { noErrorBtn?.classList.remove('wrong', 'correct'); }

        chipByIdx.forEach(({ chip, seg }) => {
          const picked = st.selectedSpans.has(seg.spanIndex);
          chip.classList.remove('correct', 'wrong', 'missed-error');
          if (seg.isError) {
            if (picked) {
              const fixOk = !se.requireFix || MBY.helpers.evaluateRecallMatch(st.fixes[seg.spanIndex] || '', seg.corrections || [], true, false) !== 'wrong';
              chip.classList.add(fixOk ? 'correct' : 'wrong');
              if (!fixOk) allOk = false;
            } else {
              chip.classList.add('missed-error');
              allOk = false;
            }
            if (seg.corrections?.length) {
              const badge = mk('span', 'mby-error-fix-badge');
              badge.innerHTML = '→ ' + (seg.primaryCorrectionHtml || '');
              ctx.renderMathIn(badge);
              chip.insertAdjacentElement('beforebegin', badge);
            }
          } else if (picked) {
            chip.classList.add('wrong');
            allOk = false;
          }
        });
      }

      st.checked = true; st.isCorrect = allOk;
      MBY.helpers.recordGradeAttempt(m, allOk);
      MBY.helpers.setFeedbackMessage(feedback, allOk ? 'Correct!' : 'Review highlighted segments.', allOk);
      retryBtn.classList.remove('hidden');
      if (explanationBox) explanationBox.classList.remove('hidden');
      if (onAnswered) onAnswered();
      requestAnimationFrame(ctx.updateScrollDownHint);
    });

    const actionRowItems = noErrorBtn ? [noErrorBtn, checkBtn, retryBtn, feedback] : [checkBtn, retryBtn, feedback];
    card.appendChild(mk('div', 'mby-card-body', null, [promptMedia, stmtEl, fixRow, mk('div', 'mby-action-row', null, actionRowItems), explanationBox]));
    return card;
  }

  MBY.gradedKinds['spot-error'] = true;
  MBY.kindLabels['spot-error'] = 'Spot the Error';
  MBY.questions['spot-error'] = buildSpotErrorCard;
  MBY.search['spot-error'] = (q, parts, ctx) => {
    if (!q.spotError) return;
    (q.spotError.segments || []).forEach(s => { parts.push(ctx.htmlToPlainText(s.html)); if (s.corrections) parts.push(s.corrections.join(' ')); });
    if (q.spotError.explanationHtml) parts.push(ctx.htmlToPlainText(q.spotError.explanationHtml));
  };
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    private const string DiffCss = """
.mby-diff-wrap { display: flex; flex-direction: column; gap: 12px; margin-bottom: 12px; }
.mby-diff-scroll { width: 100%; max-width: 100%; overflow-x: auto; border: 1px solid var(--border); border-radius: 12px; }
.mby-diff-table { width: 100%; border-collapse: collapse; font-size: 0.94em; background: var(--surface); }
.mby-diff-table thead { background: var(--accent-soft); color: var(--accent); }
.mby-diff-table th, .mby-diff-table td { padding: 10px 12px; border-bottom: 1px solid var(--border); border-right: 1px solid var(--border); vertical-align: middle; }
.mby-diff-table th:last-child, .mby-diff-table td:last-child { border-right: none; }
.mby-diff-table tbody tr:last-child td { border-bottom: none; }
.mby-diff-table tbody tr:nth-child(even) { background: var(--bg); }
.mby-diff-bank { display: flex; flex-wrap: wrap; gap: 8px; padding: 10px 12px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); }
.mby-diff-bank-chip { padding: 6px 12px; border-radius: 999px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 13px; font-weight: 600; cursor: pointer; transition: border-color 140ms ease, background 140ms ease, opacity 140ms ease; }
.mby-diff-bank-chip:hover { border-color: var(--select); }
.mby-diff-bank-chip.selected { border-color: var(--select); background: var(--select-soft); color: var(--select); }
.mby-diff-bank-chip.used { opacity: 0.45; border-style: dashed; }
.mby-diff-cell-btn { width: 100%; text-align: inherit; padding: 7px 10px; border-radius: 9px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; cursor: pointer; transition: border-color 140ms ease, background 140ms ease; }
.mby-diff-cell-btn:hover { border-color: var(--select); }
.mby-diff-cell-btn.selected, .mby-diff-cell-btn.swap-selected { border-color: var(--select); background: var(--select-soft); color: var(--select); font-weight: 650; }
.mby-diff-cell-btn.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.14); color: var(--ok); }
.mby-diff-cell-btn.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.14); color: var(--bad); }
.mby-diff-cell-btn.missed-error { border-color: var(--bad); border-style: dashed; background: rgba(220, 53, 69, 0.08); }
.mby-diff-corrections { padding: 12px 14px; border-radius: 12px; border: 1px solid var(--border); border-left: 4px solid var(--ok); background: var(--bg); font-size: 0.94em; }
.mby-diff-corrections-title { font-size: 11px; font-weight: 800; letter-spacing: 0.04em; text-transform: uppercase; color: var(--ok); margin-bottom: 6px; }
.mby-diff-correction-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.mby-diff-correction-item { display: flex; flex-wrap: wrap; align-items: baseline; gap: 6px; line-height: 1.4; }
.mby-diff-correction-loc { font-weight: 700; font-size: 12px; color: var(--muted); }
.mby-diff-wrong-strike { text-decoration: line-through; color: var(--bad); }
.mby-diff-right-text { font-weight: 700; color: var(--ok); }
""";

    private const string DiffJs = """
(function (MBY) {
  function buildDiffCard(slide, ctx, onAnswered) {
    const m = ctx.metricById.get(slide.id), d = slide.differentiation;
    const tabLabel = 'DIFFERENTIATION • ' + (d.mode || 'diff').toUpperCase();
    const card = mk('div', 'mby-card', null, [createUpperTab(tabLabel)]);
    applySlideLayoutVars(card, resolveSlideLayout(slide, 'mcq'));

    if (!ctx.gradedStateMap.has(slide.id)) {
      const initRearr = (d.rowsHtml || []).map((row, rIdx) => {
        const cols = row.slice(1).map((html, offset) => ({ colIdx: offset + 1, html, raw: d.rowsRaw?.[rIdx]?.[offset + 1] ?? '' }));
        const shuffled = d.mode === 'rearrange' ? MBY.helpers.deterministicShuffle(cols, `${slide.id}:r${rIdx}`, x => x.raw) : cols;
        return [ { colIdx: 0, html: row[0], raw: d.rowsRaw?.[rIdx]?.[0] ?? '' }, ...shuffled ];
      });
      ctx.gradedStateMap.set(slide.id, {
        values: {}, bankPickedChipIdx: null, chipBySlot: {},
        selectedCells: new Set(), pickedNoError: false,
        rearrRows: initRearr, swapSrc: null,
        checked: false, isCorrect: null, overridden: false
      });
    }
    const st = ctx.gradedStateMap.get(slide.id);

    const wrap = mk('div', 'mby-diff-wrap');
    if (d.titleHtml) {
      const titleEl = mk('div', 'mby-diff-title');
      titleEl.innerHTML = d.titleHtml; ctx.renderMathIn(titleEl);
      wrap.appendChild(titleEl);
    }

    const specialByCell = new Map((d.specialCells || []).map(sc => [`${sc.rowIndex}:${sc.colIndex}`, sc]));
    const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
    const overrideBtn = mk('button', 'mby-override-btn hidden', 'Override: Mark Correct'); overrideBtn.type = 'button';
    const feedback = mk('span');
    const explanationBox = MBY.helpers.createExplanationBox(d.explanationHtml, st.checked, ctx);

    const blankInputs = new Map();
    const expectedEls = new Map();
    const bankChips = [];
    const spotButtons = new Map();
    let noErrorBtn = null;
    const rearrButtons = new Map();
    const correctionsBox = mk('div', 'mby-diff-corrections hidden');

    const blankSpecs = (d.specialCells || []).filter(sc => sc.isBlank && sc.answers?.length);
    const bankItems = blankSpecs.map((sc, idx) => ({
      idx,
      label: sc.answers[0],
      answers: sc.answers,
      html: sc.primaryAnswerHtml || escapeHtml(sc.answers[0])
    }));
    const itemMatchesNorm = (bankItem, rawVal) => {
      const normVal = MBY.helpers.normalizeRecallAnswer(rawVal, d.caseSensitive);
      if (!normVal) return false;
      return (bankItem?.answers || []).some(ans => MBY.helpers.normalizeRecallAnswer(ans, d.caseSensitive) === normVal);
    };

    if (d.mode === 'fill' && d.bank) {
      const shuffledBank = MBY.helpers.deterministicShuffle(bankItems, `${slide.id}:bank`, x => x.label);
      const bankRow = mk('div', 'mby-diff-bank');
      shuffledBank.forEach(item => {
        const chip = mk('button', 'mby-diff-bank-chip mby-draggable'); chip.type = 'button';
        chip.draggable = true;
        chip.innerHTML = item.html; ctx.renderMathIn(chip);
        chip.dataset.chipIdx = String(item.idx);
        chip.addEventListener('click', () => {
          st.bankPickedChipIdx = (st.bankPickedChipIdx === item.idx) ? null : item.idx;
          bankChips.forEach(c => c.classList.toggle('selected', Number(c.dataset.chipIdx) === st.bankPickedChipIdx));
        });
        chip.addEventListener('dragstart', e => {
          if (e.dataTransfer) {
            e.dataTransfer.setData('application/x-mby-diff-bank', String(item.idx));
            e.dataTransfer.setData('text/plain', item.label);
            e.dataTransfer.effectAllowed = 'copyMove';
          }
          chip.classList.add('is-dragging');
        });
        chip.addEventListener('dragend', () => chip.classList.remove('is-dragging'));
        bankChips.push(chip);
        bankRow.appendChild(chip);
      });
      wrap.appendChild(bankRow);
    }

    const refreshBankUsed = () => {
      if (!(d.mode === 'fill' && d.bank)) return;
      const usedChips = new Set();
      const claimedSlots = new Set();
      blankSpecs.forEach(sc => {
        const sIdx = sc.slotIndex;
        const cid = st.chipBySlot[sIdx];
        if (cid === undefined || cid === null || usedChips.has(cid)) return;
        const item = bankItems[cid];
        if (item && itemMatchesNorm(item, st.values[sIdx] || '')) {
          usedChips.add(cid);
          claimedSlots.add(sIdx);
        } else {
          delete st.chipBySlot[sIdx];
        }
      });
      blankSpecs.forEach(sc => {
        const sIdx = sc.slotIndex;
        if (claimedSlots.has(sIdx)) return;
        const val = st.values[sIdx] || '';
        if (!val.trim()) return;
        const matchItem = bankItems.find(it => !usedChips.has(it.idx) && itemMatchesNorm(it, val));
        if (matchItem) {
          usedChips.add(matchItem.idx);
          st.chipBySlot[sIdx] = matchItem.idx;
          claimedSlots.add(sIdx);
        }
      });
      bankChips.forEach(c => c.classList.toggle('used', usedChips.has(Number(c.dataset.chipIdx))));
    };

    const applyBankChipToSlot = (chipIdx, sIdx, inp) => {
      const bankItem = bankItems[chipIdx];
      if (!bankItem || !inp) return;
      for (const [k, v] of Object.entries(st.chipBySlot)) {
        if (v === chipIdx) delete st.chipBySlot[k];
      }
      inp.value = bankItem.label;
      st.values[sIdx] = inp.value;
      st.chipBySlot[sIdx] = chipIdx;
      st.bankPickedChipIdx = null;
      bankChips.forEach(ch => ch.classList.remove('selected'));
      refreshBankUsed();
    };

    const scroll = mk('div', 'mby-diff-scroll');
    const tableEl = mk('table', 'mby-diff-table');
    const thead = mk('thead');
    const headTr = mk('tr');
    (d.headersHtml || []).forEach((hHtml, idx) => {
      const th = mk('th');
      if (d.alignments?.[idx]) th.style.textAlign = d.alignments[idx];
      th.innerHTML = hHtml; ctx.renderMathIn(th);
      headTr.appendChild(th);
    });
    thead.appendChild(headTr);
    tableEl.appendChild(thead);

    const swapRearrCells = (r, c1, c2) => {
      const rowArr = st.rearrRows[r];
      const tmp = rowArr[c1]; rowArr[c1] = rowArr[c2]; rowArr[c2] = tmp;
      const b1 = rearrButtons.get(`${r}:${c1}`), b2 = rearrButtons.get(`${r}:${c2}`);
      if (b1 && b2) {
        const tmpHtml = b1.innerHTML;
        b1.innerHTML = b2.innerHTML;
        b2.innerHTML = tmpHtml;
      }
      st.swapSrc = null;
      rearrButtons.forEach(b => b.classList.remove('swap-selected'));
    };

    const tbody = mk('tbody');
    const renderTableBody = () => {
      tbody.innerHTML = '';
      blankInputs.clear(); expectedEls.clear(); spotButtons.clear(); rearrButtons.clear();
      const rowCount = (d.rowsHtml || []).length;
      const fillPlaceholderAttr = d.placeholder ? ` placeholder="${escapeHtml(d.placeholder)}"` : '';
      for (let r = 0; r < rowCount; r++) {
        const tr = mk('tr');
        const cols = d.mode === 'rearrange' ? st.rearrRows[r] : (d.rowsHtml[r] || []).map((html, c) => ({ colIdx: c, html }));
        for (let c = 0; c < cols.length; c++) {
          const td = mk('td');
          if (d.alignments?.[c]) td.style.textAlign = d.alignments[c];
          const spec = specialByCell.get(`${r}:${c}`);

          if (d.mode === 'fill' && spec && spec.isBlank) {
            const sIdx = spec.slotIndex;
            const cellHtml = (cols[c].html || '').replace(`MBYDIFFSLOT${sIdx}END`, `<span class="mby-blank-wrap"><input type="text" class="mby-blank-input" data-diff-slot="${sIdx}" autocomplete="off" spellcheck="false"${fillPlaceholderAttr} /><span class="mby-blank-expected hidden" data-diff-exp="${sIdx}"></span></span>`);
            const host = mk('div'); host.innerHTML = cellHtml; ctx.renderMathIn(host);
            const inp = host.querySelector('.mby-blank-input');
            const expEl = host.querySelector('.mby-blank-expected');
            if (inp) {
              if (st.values[sIdx] !== undefined) inp.value = st.values[sIdx];
              inp.addEventListener('input', () => {
                st.values[sIdx] = inp.value;
                delete st.chipBySlot[sIdx];
                refreshBankUsed();
              });
              inp.addEventListener('click', () => {
                if (d.bank && st.bankPickedChipIdx !== null) {
                  applyBankChipToSlot(st.bankPickedChipIdx, sIdx, inp);
                }
              });
              if (d.bank) {
                MBY.helpers.bindDropTarget(inp, ['application/x-mby-diff-bank', 'text/plain'], e => {
                  const rawChipId = e.dataTransfer?.getData('application/x-mby-diff-bank');
                  if (rawChipId !== undefined && rawChipId !== '') {
                    const chipIdx = Number(rawChipId);
                    if (!Number.isNaN(chipIdx)) { applyBankChipToSlot(chipIdx, sIdx, inp); return; }
                  }
                  const droppedText = e.dataTransfer?.getData('text/plain');
                  if (droppedText) {
                    inp.value = droppedText;
                    st.values[sIdx] = inp.value;
                    delete st.chipBySlot[sIdx];
                    refreshBankUsed();
                  }
                });
              }
              inp.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); checkBtn.click(); } });
              blankInputs.set(sIdx, inp);
            }
            if (expEl) expectedEls.set(sIdx, expEl);
            td.appendChild(host);
          } else if (d.mode === 'spot-wrong' && c > 0) {
            const key = `${r}:${c}`;
            const btn = mk('button', 'mby-diff-cell-btn' + (st.selectedCells.has(key) ? ' selected' : ''));
            btn.type = 'button';
            btn.innerHTML = cols[c].html; ctx.renderMathIn(btn);
            btn.addEventListener('click', () => {
              st.pickedNoError = false;
              if (noErrorBtn) noErrorBtn.classList.remove('selected');
              if (st.selectedCells.has(key)) { st.selectedCells.delete(key); btn.classList.remove('selected'); }
              else { st.selectedCells.add(key); btn.classList.add('selected'); }
            });
            spotButtons.set(key, btn);
            td.appendChild(btn);
          } else if (d.mode === 'rearrange' && c > 0) {
            const key = `${r}:${c}`;
            const isSrc = st.swapSrc && st.swapSrc.r === r && st.swapSrc.c === c;
            const btn = mk('button', 'mby-diff-cell-btn mby-draggable' + (isSrc ? ' swap-selected' : ''));
            btn.type = 'button';
            btn.draggable = true;
            btn.innerHTML = cols[c].html; ctx.renderMathIn(btn);
            btn.addEventListener('dragstart', e => {
              if (e.dataTransfer) {
                e.dataTransfer.setData('application/x-mby-diff-rearr', JSON.stringify({ r, c }));
                e.dataTransfer.effectAllowed = 'move';
              }
              btn.classList.add('is-dragging');
            });
            btn.addEventListener('dragend', () => btn.classList.remove('is-dragging'));
            MBY.helpers.bindDropTarget(btn, 'application/x-mby-diff-rearr', e => {
              try {
                const src = JSON.parse(e.dataTransfer?.getData('application/x-mby-diff-rearr') || 'null');
                if (src && src.r === r && src.c !== c) swapRearrCells(r, src.c, c);
              } catch {}
            });
            btn.addEventListener('click', () => {
              if (!st.swapSrc || st.swapSrc.r !== r) {
                st.swapSrc = { r, c };
                rearrButtons.forEach((b, k) => b.classList.toggle('swap-selected', k === key));
              } else if (st.swapSrc.c === c) {
                st.swapSrc = null;
                btn.classList.remove('swap-selected');
              } else {
                swapRearrCells(r, st.swapSrc.c, c);
              }
            });
            rearrButtons.set(key, btn);
            td.appendChild(btn);
          } else {
            const cellDiv = mk('div'); cellDiv.innerHTML = cols[c].html; ctx.renderMathIn(cellDiv);
            td.appendChild(cellDiv);
          }
          tr.appendChild(td);
        }
        tbody.appendChild(tr);
      }
      refreshBankUsed();
    };

    renderTableBody();
    tableEl.appendChild(tbody);
    scroll.appendChild(tableEl);
    wrap.appendChild(scroll);
    wrap.appendChild(correctionsBox);

    if (d.mode === 'spot-wrong' && d.allowNoError) {
      noErrorBtn = mk('button', 'mby-no-error-btn' + (st.pickedNoError ? ' selected' : ''), 'No error in this table');
      noErrorBtn.type = 'button';
      noErrorBtn.addEventListener('click', () => {
        st.pickedNoError = !st.pickedNoError;
        noErrorBtn.classList.toggle('selected', st.pickedNoError);
        if (st.pickedNoError) {
          st.selectedCells.clear();
          spotButtons.forEach(b => b.classList.remove('selected'));
        }
      });
    }

    const retryBtn = MBY.helpers.createRetryBtn(() => {
      st.values = {};
      st.bankPickedChipIdx = null;
      st.chipBySlot = {};
      st.selectedCells.clear();
      st.pickedNoError = false;
      st.swapSrc = null;
      st.rearrRows = (d.rowsHtml || []).map((row, rIdx) => {
        const cols = row.slice(1).map((html, offset) => ({ colIdx: offset + 1, html, raw: d.rowsRaw?.[rIdx]?.[offset + 1] ?? '' }));
        const shuffled = d.mode === 'rearrange' ? MBY.helpers.deterministicShuffle(cols, `${slide.id}:r${rIdx}`, x => x.raw) : cols;
        return [{ colIdx: 0, html: row[0], raw: d.rowsRaw?.[rIdx]?.[0] ?? '' }, ...shuffled];
      });
      st.checked = false;
      st.isCorrect = null;
      st.overridden = false;
      wrap.querySelectorAll('.mby-diff-fix-badge').forEach(el => el.remove());
      correctionsBox.innerHTML = '';
      correctionsBox.classList.add('hidden');
      if (noErrorBtn) noErrorBtn.classList.remove('selected', 'correct', 'wrong');
      bankChips.forEach(c => c.classList.remove('selected', 'used'));
      overrideBtn.classList.add('hidden');
      feedback.textContent = '';
      if (explanationBox) explanationBox.classList.add('hidden');
      retryBtn.classList.add('hidden');
      if (d.mode === 'rearrange' && rearrButtons.size > 0) {
        rearrButtons.forEach((btn, key) => {
          const [rStr, cStr] = key.split(':');
          const cell = st.rearrRows[Number(rStr)]?.[Number(cStr)];
          btn.classList.remove('correct', 'wrong', 'swap-selected');
          if (cell) { btn.innerHTML = cell.html; ctx.renderMathIn(btn); }
        });
      } else {
        renderTableBody();
      }
      if (m) m.isCorrect = null;
      if (onAnswered) onAnswered();
      requestAnimationFrame(ctx.updateScrollDownHint);
    }, st.checked);

    checkBtn.addEventListener('click', () => {
      let allOk = true, anyFuzzy = false;
      wrap.querySelectorAll('.mby-diff-fix-badge').forEach(el => el.remove());
      correctionsBox.innerHTML = '';
      correctionsBox.classList.add('hidden');

      if (d.mode === 'fill') {
        (d.specialCells || []).filter(sc => sc.isBlank).forEach(sc => {
          const sIdx = sc.slotIndex, inp = blankInputs.get(sIdx), expEl = expectedEls.get(sIdx);
          const val = inp ? inp.value : (st.values[sIdx] || '');
          st.values[sIdx] = val;
          const verdict = MBY.helpers.evaluateRecallMatch(val, sc.answers || [], d.fuzzy, d.caseSensitive);
          if (inp) inp.classList.remove('correct', 'fuzzy-ok', 'wrong');
          if (verdict === 'exact') { inp?.classList.add('correct'); expEl?.classList.add('hidden'); }
          else if (verdict === 'fuzzy') { inp?.classList.add('fuzzy-ok'); anyFuzzy = true; if (expEl) { expEl.innerHTML = 'Accepted: ' + (sc.primaryAnswerHtml || ''); ctx.renderMathIn(expEl); expEl.classList.remove('hidden'); } }
          else { inp?.classList.add('wrong'); allOk = false; if (expEl) { expEl.innerHTML = 'Answer: ' + (sc.primaryAnswerHtml || ''); ctx.renderMathIn(expEl); expEl.classList.remove('hidden'); } }
        });
        overrideBtn.classList.toggle('hidden', allOk);
        feedback.textContent = allOk ? (anyFuzzy ? 'Almost Correct!' : 'Correct!') : 'Review highlighted cells.';
      } else if (d.mode === 'spot-wrong') {
        const errorCells = (d.specialCells || []).filter(sc => sc.isError);
        const fixItems = [];
        if (errorCells.length === 0) {
          allOk = st.pickedNoError && st.selectedCells.size === 0;
          if (noErrorBtn) { noErrorBtn.classList.remove('correct', 'wrong'); noErrorBtn.classList.add('correct'); }
          spotButtons.forEach((btn, key) => {
            btn.classList.remove('correct', 'wrong', 'missed-error');
            if (st.selectedCells.has(key)) btn.classList.add('wrong');
          });
        } else {
          if (st.pickedNoError) { allOk = false; noErrorBtn?.classList.add('wrong'); }
          else noErrorBtn?.classList.remove('wrong', 'correct');
          spotButtons.forEach((btn, key) => {
            const picked = st.selectedCells.has(key);
            const sc = specialByCell.get(key);
            btn.classList.remove('correct', 'wrong', 'missed-error');
            if (sc && sc.isError) {
              if (picked) btn.classList.add('correct');
              else { btn.classList.add('missed-error'); allOk = false; }
              if (sc.primaryCorrectionHtml) fixItems.push(sc);
            } else if (picked) {
              btn.classList.add('wrong');
              allOk = false;
            }
          });
        }
        if (fixItems.length > 0) {
          correctionsBox.appendChild(mk('div', 'mby-diff-corrections-title', 'Corrections'));
          const ul = mk('ul', 'mby-diff-correction-list');
          fixItems.forEach(sc => {
            const li = mk('li', 'mby-diff-correction-item');
            const rowLabel = ctx.htmlToPlainText(d.rowsHtml[sc.rowIndex]?.[0] || `Row ${sc.rowIndex + 1}`);
            const colLabel = ctx.htmlToPlainText(d.headersHtml[sc.colIndex] || `Col ${sc.colIndex + 1}`);
            li.appendChild(mk('span', 'mby-diff-correction-loc', `${rowLabel} • ${colLabel}:`));
            const wrongSpan = mk('span', 'mby-diff-wrong-strike');
            wrongSpan.innerHTML = d.rowsHtml[sc.rowIndex]?.[sc.colIndex] || '';
            ctx.renderMathIn(wrongSpan);
            const rightSpan = mk('span', 'mby-diff-right-text');
            rightSpan.innerHTML = '→ ' + sc.primaryCorrectionHtml;
            ctx.renderMathIn(rightSpan);
            li.appendChild(wrongSpan);
            li.appendChild(rightSpan);
            ul.appendChild(li);
          });
          correctionsBox.appendChild(ul);
          correctionsBox.classList.remove('hidden');
        }
        feedback.textContent = allOk ? 'Correct!' : 'Review highlighted cells.';
      } else if (d.mode === 'rearrange') {
        for (let r = 0; r < (d.rowsRaw || []).length; r++) {
          for (let c = 1; c < (d.rowsRaw[r] || []).length; c++) {
            const key = `${r}:${c}`, btn = rearrButtons.get(key);
            const currentVal = st.rearrRows[r]?.[c]?.raw ?? '';
            const expectedVal = d.rowsRaw[r][c] ?? '';
            const cellOk = currentVal === expectedVal;
            btn?.classList.remove('correct', 'wrong', 'swap-selected');
            btn?.classList.add(cellOk ? 'correct' : 'wrong');
            if (!cellOk) {
              allOk = false;
              const badge = mk('div', 'mby-diff-fix-badge');
              badge.innerHTML = '→ ' + (d.rowsHtml[r]?.[c] || escapeHtml(expectedVal)); ctx.renderMathIn(badge);
              btn?.insertAdjacentElement('afterend', badge);
            }
          }
        }
        feedback.textContent = allOk ? 'Correct!' : 'Review swapped cells.';
      }

      MBY.helpers.setFeedbackMessage(feedback, feedback.textContent, allOk);
      st.checked = true; st.isCorrect = allOk; st.overridden = false;
      retryBtn.classList.remove('hidden');
      MBY.helpers.recordGradeAttempt(m, allOk);
      if (explanationBox) explanationBox.classList.remove('hidden');
      if (onAnswered) onAnswered();
      requestAnimationFrame(ctx.updateScrollDownHint);
    });

    overrideBtn.addEventListener('click', () => {
      st.isCorrect = true; st.overridden = true;
      blankInputs.forEach(inp => { inp.classList.remove('wrong'); inp.classList.add('correct'); });
      if (m) m.isCorrect = true;
      feedback.textContent = 'Marked correct (self-override).';
      feedback.style.color = 'var(--ok)';
      overrideBtn.classList.add('hidden');
      retryBtn.classList.remove('hidden');
      if (onAnswered) onAnswered();
    });

    const actionRowItems = noErrorBtn ? [noErrorBtn, checkBtn, overrideBtn, retryBtn, feedback] : [checkBtn, overrideBtn, retryBtn, feedback];
    card.appendChild(mk('div', 'mby-card-body', null, [wrap, mk('div', 'mby-action-row', null, actionRowItems), explanationBox]));
    return card;
  }

  MBY.gradedKinds['diff'] = true;
  MBY.kindLabels['diff'] = 'Differentiation';
  MBY.questions['diff'] = buildDiffCard;
  MBY.search['diff'] = (q, parts, ctx) => {
    if (!q.differentiation) return;
    if (q.differentiation.titleHtml) parts.push(ctx.htmlToPlainText(q.differentiation.titleHtml));
    (q.differentiation.headersHtml || []).forEach(h => parts.push(ctx.htmlToPlainText(h)));
    (q.differentiation.rowsHtml || []).forEach(r => r.forEach(c => parts.push(ctx.htmlToPlainText(c).replace(/MBYDIFFSLOT\d+END/g, ' ___ '))));
    (q.differentiation.specialCells || []).forEach(sc => { if (sc.answers) parts.push(sc.answers.join(' ')); if (sc.corrections) parts.push(sc.corrections.join(' ')); });
    if (q.differentiation.explanationHtml) parts.push(ctx.htmlToPlainText(q.differentiation.explanationHtml));
  };
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    private const string RelabelCss = """
.mby-relabel-layout { display: flex; flex-direction: column; gap: 12px; margin-bottom: 12px; }
.mby-relabel-prompt { font-size: var(--prompt-font-size, 22px); font-weight: 700; line-height: 1.4; }
.mby-relabel-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.mby-relabel-tool-btn { padding: 6px 12px; border-radius: 9px; border: 1px solid var(--border); background: var(--bg); color: var(--text); font: inherit; font-size: 13px; font-weight: 650; cursor: pointer; }
.mby-relabel-tool-btn:hover { border-color: var(--select); color: var(--select); }
.mby-relabel-stage-wrap { display: flex; justify-content: center; width: 100%; }
.mby-relabel-stage { position: relative; display: inline-block; max-width: 100%; line-height: 0; border-radius: 12px; overflow: hidden; border: 1px solid var(--border); }
.mby-relabel-img { display: block; max-width: 100%; max-height: var(--image-max-height, 360px); width: auto; height: auto; user-select: none; }
.mby-mask-box { position: absolute; box-sizing: border-box; padding: 0; margin: 0; border-radius: 6px; cursor: pointer; display: flex; align-items: center; justify-content: center; overflow: hidden; transition: border-color 140ms ease, background 140ms ease, box-shadow 140ms ease; }
.mby-mask-solid { background: var(--accent); border: 1.5px solid rgba(255, 255, 255, 0.7); }
.mby-mask-blur { background: rgba(15, 23, 42, 0.22); backdrop-filter: blur(8px); -webkit-backdrop-filter: blur(8px); border: 1.5px solid var(--accent); }
.mby-mask-pixelate { background-image: repeating-conic-gradient(#334155 0% 25%, #94a3b8 0% 50%); background-size: 8px 8px; border: 1.5px solid #1e293b; }
.mby-mask-whiteout { background: #ffffff; border: 1.5px solid var(--border); }
.mby-mask-box.active { box-shadow: inset 0 0 0 2px #ffffff, 0 0 0 2px var(--select); z-index: 2; }
.mby-mask-box.revealed, .mby-mask-box.correct { background: rgba(25, 135, 84, 0.14); background-image: none; backdrop-filter: none; -webkit-backdrop-filter: none; border: 2px solid var(--ok); }
.mby-mask-box.wrong { background: rgba(220, 53, 69, 0.16); background-image: none; backdrop-filter: none; -webkit-backdrop-filter: none; border: 2px solid var(--bad); }
.mby-mask-badge { display: inline-flex; align-items: center; justify-content: center; min-width: 18px; height: 18px; padding: 0 5px; border-radius: 999px; background: rgba(15, 23, 42, 0.86); color: #ffffff; font-size: 11px; font-weight: 800; line-height: 1; pointer-events: none; max-width: 100%; overflow: hidden; }
.mby-mask-box.revealed .mby-mask-badge, .mby-mask-box.correct .mby-mask-badge { background: var(--ok); }
.mby-mask-box.wrong .mby-mask-badge { background: var(--bad); }
.mby-relabel-assignments { display: flex; flex-direction: column; gap: 6px; padding: 10px 12px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); }
.mby-relabel-assign-row { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; padding: 6px 10px; border-radius: 9px; border: 1.5px solid var(--border); background: var(--surface); cursor: pointer; font-size: 14px; }
.mby-relabel-assign-row.active { border-color: var(--select); background: var(--select-soft); }
.mby-relabel-assign-row.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.12); }
.mby-relabel-assign-row.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.12); }
.mby-relabel-bank { display: flex; flex-wrap: wrap; gap: 8px; padding: 10px 12px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); }
.mby-relabel-bank-chip { padding: 6px 12px; border-radius: 999px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 13px; font-weight: 600; cursor: pointer; }
.mby-relabel-bank-chip:hover { border-color: var(--select); }
.mby-relabel-bank-chip.used { opacity: 0.45; border-style: dashed; }
.mby-relabel-recall-panel { display: flex; flex-direction: column; gap: 8px; padding: 10px 12px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); }
.mby-relabel-recall-row { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.mby-relabel-recall-input { flex: 1; min-width: 160px; padding: 7px 10px; border-radius: 9px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 14px; }
.mby-relabel-recall-input:focus { outline: none; border-color: var(--select); }
.mby-relabel-recall-input.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.12); }
.mby-relabel-recall-input.fuzzy-ok { border-color: var(--callout-pearl); background: var(--callout-pearl-bg); }
.mby-relabel-recall-input.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.12); }
""";

    private const string RelabelJs = """
(function (MBY) {
  function buildRelabelCard(slide, ctx, onAnswered) {
    const m = ctx.metricById.get(slide.id), rel = slide.relabel;
    const mode = rel.mode || 'bank', styleCls = 'mby-mask-' + (rel.style || 'solid');
    const boxes = rel.boxes || [];
    const card = mk('div', 'mby-card', null, [createUpperTab('RELABEL THE IMAGE • ' + mode.toUpperCase())]);
    applySlideLayoutVars(card, resolveSlideLayout(slide, 'mcq'));

    if (!ctx.gradedStateMap.has(slide.id)) {
      const bankPool = mode === 'bank'
        ? MBY.helpers.deterministicShuffle([...boxes.map(b => b.label), ...(rel.distractors || [])].map((lbl, idx) => ({ chipId: idx, label: lbl })), `${slide.id}:relbank`, it => `${it.chipId}:${it.label}`)
        : [];
      ctx.gradedStateMap.set(slide.id, {
        activeBoxIdx: boxes[0]?.index ?? null,
        revealed: new Set(),
        assignedChipByBox: {},
        bankPool,
        recallValues: {},
        checked: false,
        isCorrect: null,
        rating: null,
        zoomUnlocked: false
      });
    }
    const st = ctx.gradedStateMap.get(slide.id);

    const layout = mk('div', 'mby-relabel-layout');
    if (rel.promptHtml) {
      const pEl = mk('div', 'mby-relabel-prompt');
      pEl.innerHTML = rel.promptHtml; ctx.renderMathIn(pEl);
      layout.appendChild(pEl);
    }

    const toolbar = mk('div', 'mby-relabel-toolbar');
    const zoomBtn = mk('button', 'mby-relabel-tool-btn' + (st.zoomUnlocked ? '' : ' hidden'), 'Zoom Image');
    zoomBtn.type = 'button';
    zoomBtn.addEventListener('click', () => ctx.openZoomLightbox(rel.imagePath, ctx.htmlToPlainText(rel.promptHtml) || 'Relabel diagram'));

    const stage = mk('div', 'mby-relabel-stage');
    const img = mk('img', 'mby-relabel-img');
    img.decoding = 'async';
    img.loading = 'lazy';
    img.src = rel.imagePath;
    img.alt = ctx.htmlToPlainText(rel.promptHtml) || 'Diagram to relabel';
    img.draggable = false;
    img.addEventListener('load', () => requestAnimationFrame(ctx.updateScrollDownHint));
    stage.appendChild(img);

    const boxBtnMap = new Map();
    const recallInputMap = new Map();
    const recallExpectedMap = new Map();
    let assignListEl = null, bankEl = null;

    const unlockZoom = () => { st.zoomUnlocked = true; zoomBtn.classList.remove('hidden'); };

    const explanationBox = MBY.helpers.createExplanationBox(rel.explanationHtml, st.checked, ctx);

    const syncBoxVisuals = () => {
      boxes.forEach(b => {
        const btn = boxBtnMap.get(b.index);
        if (!btn) return;
        btn.classList.toggle('active', mode !== 'reveal' && st.activeBoxIdx === b.index);
        if (mode === 'reveal') btn.classList.toggle('revealed', st.revealed.has(b.index));
      });
    };

    let renderBankUi = null;
    let assignChipToBox = null;
    let actionRow = null;

    boxes.forEach(b => {
      const btn = mk('button', 'mby-mask-box ' + styleCls);
      btn.type = 'button';
      btn.style.left = (b.x * 100) + '%';
      btn.style.top = (b.y * 100) + '%';
      btn.style.width = (b.w * 100) + '%';
      btn.style.height = (b.h * 100) + '%';
      btn.title = 'Mask #' + b.index;
      btn.setAttribute('aria-label', 'Mask ' + b.index);
      btn.appendChild(mk('span', 'mby-mask-badge', String(b.index)));
      btn.addEventListener('click', () => {
        if (mode === 'reveal') {
          if (st.revealed.has(b.index)) st.revealed.delete(b.index); else st.revealed.add(b.index);
          if (st.revealed.size >= boxes.length) { unlockZoom(); if (explanationBox) explanationBox.classList.remove('hidden'); }
          syncBoxVisuals();
        } else if (mode === 'bank') {
          st.activeBoxIdx = b.index;
          syncBoxVisuals();
          if (renderBankUi) renderBankUi();
        } else {
          st.activeBoxIdx = b.index;
          syncBoxVisuals();
          recallInputMap.get(b.index)?.focus();
        }
      });
      if (mode === 'bank') {
        MBY.helpers.bindDropTarget(btn, 'application/x-mby-relabel-chip', e => {
          const raw = e.dataTransfer?.getData('application/x-mby-relabel-chip');
          if (raw !== undefined && raw !== '' && assignChipToBox) {
            const cid = Number(raw);
            if (!Number.isNaN(cid)) assignChipToBox(cid, b.index);
          }
        });
      }
      boxBtnMap.set(b.index, btn);
      stage.appendChild(btn);
    });

    if (mode === 'reveal') {
      const revAllBtn = mk('button', 'mby-relabel-tool-btn', 'Reveal All'); revAllBtn.type = 'button';
      const hideAllBtn = mk('button', 'mby-relabel-tool-btn', 'Hide All'); hideAllBtn.type = 'button';
      revAllBtn.addEventListener('click', () => {
        boxes.forEach(b => st.revealed.add(b.index));
        unlockZoom();
        if (explanationBox) explanationBox.classList.remove('hidden');
        syncBoxVisuals();
      });
      hideAllBtn.addEventListener('click', () => {
        st.revealed.clear();
        syncBoxVisuals();
      });
      toolbar.appendChild(revAllBtn);
      toolbar.appendChild(hideAllBtn);
    }
    toolbar.appendChild(zoomBtn);
    layout.appendChild(toolbar);
    layout.appendChild(mk('div', 'mby-relabel-stage-wrap', null, [stage]));

    if (mode === 'reveal') {
      const knownBtn = mk('button', 'mby-rate-btn' + (st.rating === 'known' ? ' active-known' : ''), 'Got It'); knownBtn.type = 'button';
      const reviewBtn = mk('button', 'mby-rate-btn' + (st.rating === 'needsReview' ? ' active-review' : ''), 'Needs Review'); reviewBtn.type = 'button';
      const feedback = mk('span');
      const retryBtn = MBY.helpers.createRetryBtn(() => {
        st.rating = null; st.checked = false; st.isCorrect = null;
        st.revealed.clear();
        knownBtn.classList.remove('active-known');
        reviewBtn.classList.remove('active-review');
        boxBtnMap.forEach(btn => btn.classList.remove('revealed', 'correct', 'wrong'));
        syncBoxVisuals();
        feedback.textContent = '';
        if (explanationBox) explanationBox.classList.add('hidden');
        retryBtn.classList.add('hidden');
        if (m) { m.isCorrect = null; m.flashcardRating = null; }
        if (onAnswered) onAnswered();
        requestAnimationFrame(ctx.updateScrollDownHint);
      }, !!st.rating);
      const rate = r => {
        const ok = r === 'known';
        st.rating = r; st.checked = true; st.isCorrect = ok;
        unlockZoom();
        knownBtn.classList.toggle('active-known', ok);
        reviewBtn.classList.toggle('active-review', !ok);
        MBY.helpers.recordGradeAttempt(m, ok);
        if (m) m.flashcardRating = r;
        MBY.helpers.setFeedbackMessage(feedback, ok ? 'Marked as known!' : 'Flagged for review.', ok);
        retryBtn.classList.remove('hidden');
        if (explanationBox) explanationBox.classList.remove('hidden');
        if (onAnswered) onAnswered();
        requestAnimationFrame(ctx.updateScrollDownHint);
      };
      knownBtn.addEventListener('click', () => rate('known'));
      reviewBtn.addEventListener('click', () => rate('needsReview'));
      actionRow = mk('div', 'mby-action-row', null, [knownBtn, reviewBtn, retryBtn, feedback]);
    } else if (mode === 'bank') {
      assignListEl = mk('div', 'mby-relabel-assignments');
      bankEl = mk('div', 'mby-relabel-bank');
      const chipById = new Map(st.bankPool.map(it => [it.chipId, it]));
      const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
      const feedback = mk('span');

      assignChipToBox = (chipId, targetBoxIdx) => {
        const destIdx = targetBoxIdx ?? st.activeBoxIdx ?? boxes[0]?.index ?? null;
        if (destIdx === null) return;
        for (const [k, v] of Object.entries(st.assignedChipByBox)) {
          if (v === chipId) delete st.assignedChipByBox[k];
        }
        st.assignedChipByBox[destIdx] = chipId;
        st.checked = false;
        st.isCorrect = null;
        boxBtnMap.forEach(btn => btn.classList.remove('correct', 'wrong'));
        feedback.textContent = '';
        if (explanationBox) explanationBox.classList.add('hidden');
        const nextEmpty = boxes.find(b => st.assignedChipByBox[b.index] === undefined);
        if (nextEmpty) st.activeBoxIdx = nextEmpty.index;
        syncBoxVisuals();
        renderBankUi();
      };

      const retryBtn = MBY.helpers.createRetryBtn(() => {
        st.assignedChipByBox = {};
        st.activeBoxIdx = boxes[0]?.index ?? null;
        st.checked = false;
        st.isCorrect = null;
        boxBtnMap.forEach(btn => btn.classList.remove('correct', 'wrong', 'revealed'));
        feedback.textContent = '';
        if (explanationBox) explanationBox.classList.add('hidden');
        retryBtn.classList.add('hidden');
        syncBoxVisuals();
        renderBankUi();
        if (m) m.isCorrect = null;
        if (onAnswered) onAnswered();
        requestAnimationFrame(ctx.updateScrollDownHint);
      }, st.checked);

      renderBankUi = () => {
        assignListEl.innerHTML = '';
        boxes.forEach(b => {
          const assignedId = st.assignedChipByBox[b.index];
          const assignedItem = assignedId !== undefined ? chipById.get(assignedId) : null;
          const row = mk('div', 'mby-relabel-assign-row' + (st.activeBoxIdx === b.index ? ' active' : ''));
          row.appendChild(mk('span', 'mby-mech-badge', String(b.index)));
          row.appendChild(mk('span', null, assignedItem ? assignedItem.label : '(Click or drag a label below)'));
          if (st.checked) {
            const ok = assignedItem && MBY.helpers.evaluateRecallMatch(assignedItem.label, [b.label, ...(b.aliases || [])], false, rel.caseSensitive) !== 'wrong';
            row.classList.add(ok ? 'correct' : 'wrong');
            if (!ok) row.appendChild(mk('span', 'mby-diff-fix-badge', 'Answer: ' + b.label));
          }
          row.addEventListener('click', () => {
            st.activeBoxIdx = b.index;
            syncBoxVisuals();
            renderBankUi();
          });
          MBY.helpers.bindDropTarget(row, 'application/x-mby-relabel-chip', e => {
            const raw = e.dataTransfer?.getData('application/x-mby-relabel-chip');
            if (raw !== undefined && raw !== '') {
              const cid = Number(raw);
              if (!Number.isNaN(cid)) assignChipToBox(cid, b.index);
            }
          });
          assignListEl.appendChild(row);
        });

        const usedIds = new Set(Object.values(st.assignedChipByBox).filter(v => v !== undefined && v !== null));
        bankEl.innerHTML = '';
        st.bankPool.forEach(item => {
          const chip = mk('button', 'mby-relabel-bank-chip mby-draggable' + (usedIds.has(item.chipId) ? ' used' : ''), item.label);
          chip.type = 'button';
          chip.draggable = true;
          chip.addEventListener('dragstart', e => {
            if (e.dataTransfer) {
              e.dataTransfer.setData('application/x-mby-relabel-chip', String(item.chipId));
              e.dataTransfer.effectAllowed = 'copyMove';
            }
            chip.classList.add('is-dragging');
          });
          chip.addEventListener('dragend', () => chip.classList.remove('is-dragging'));
          chip.addEventListener('click', () => assignChipToBox(item.chipId, null));
          bankEl.appendChild(chip);
        });
      };

      checkBtn.addEventListener('click', () => {
        let allOk = true;
        boxes.forEach(b => {
          const assignedId = st.assignedChipByBox[b.index];
          const assignedItem = assignedId !== undefined ? chipById.get(assignedId) : null;
          const ok = !!assignedItem && MBY.helpers.evaluateRecallMatch(assignedItem.label, [b.label, ...(b.aliases || [])], false, rel.caseSensitive) !== 'wrong';
          const btn = boxBtnMap.get(b.index);
          btn?.classList.remove('correct', 'wrong', 'revealed');
          btn?.classList.add(ok ? 'correct' : 'wrong');
          if (!ok) allOk = false;
        });
        st.checked = true; st.isCorrect = allOk;
        unlockZoom();
        renderBankUi();
        MBY.helpers.recordGradeAttempt(m, allOk);
        MBY.helpers.setFeedbackMessage(feedback, allOk ? 'All labels correct!' : 'Review highlighted boxes.', allOk);
        retryBtn.classList.remove('hidden');
        if (explanationBox) explanationBox.classList.remove('hidden');
        if (onAnswered) onAnswered();
        requestAnimationFrame(ctx.updateScrollDownHint);
      });

      renderBankUi();
      layout.appendChild(assignListEl);
      layout.appendChild(bankEl);
      actionRow = mk('div', 'mby-action-row', null, [checkBtn, retryBtn, feedback]);
    } else {
      const recallPanel = mk('div', 'mby-relabel-recall-panel');
      const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
      const overrideBtn = mk('button', 'mby-override-btn hidden', 'Override: Mark Correct'); overrideBtn.type = 'button';
      const feedback = mk('span');

      boxes.forEach(b => {
        const row = mk('div', 'mby-relabel-recall-row');
        row.appendChild(mk('span', 'mby-mech-badge', String(b.index)));
        const inp = mk('input', 'mby-relabel-recall-input');
        inp.type = 'text'; inp.autocomplete = 'off'; inp.spellcheck = false; inp.placeholder = 'Label #' + b.index;
        if (st.recallValues[b.index]) inp.value = st.recallValues[b.index];
        inp.addEventListener('focus', () => { st.activeBoxIdx = b.index; syncBoxVisuals(); });
        inp.addEventListener('input', () => { st.recallValues[b.index] = inp.value; });
        inp.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); checkBtn.click(); } });
        const expEl = mk('span', 'mby-blank-expected hidden');
        recallInputMap.set(b.index, inp);
        recallExpectedMap.set(b.index, expEl);
        row.appendChild(inp);
        row.appendChild(expEl);
        recallPanel.appendChild(row);
      });

      const retryBtn = MBY.helpers.createRetryBtn(() => {
        st.recallValues = {};
        st.activeBoxIdx = boxes[0]?.index ?? null;
        st.checked = false;
        st.isCorrect = null;
        boxes.forEach(b => {
          const inp = recallInputMap.get(b.index), expEl = recallExpectedMap.get(b.index), btn = boxBtnMap.get(b.index);
          if (inp) { inp.value = ''; inp.classList.remove('correct', 'fuzzy-ok', 'wrong'); }
          if (expEl) { expEl.textContent = ''; expEl.classList.add('hidden'); }
          btn?.classList.remove('correct', 'wrong', 'revealed');
        });
        syncBoxVisuals();
        overrideBtn.classList.add('hidden');
        feedback.textContent = '';
        if (explanationBox) explanationBox.classList.add('hidden');
        retryBtn.classList.add('hidden');
        if (m) m.isCorrect = null;
        if (onAnswered) onAnswered();
        requestAnimationFrame(ctx.updateScrollDownHint);
      }, st.checked);

      checkBtn.addEventListener('click', () => {
        let allOk = true, anyFuzzy = false;
        boxes.forEach(b => {
          const inp = recallInputMap.get(b.index), expEl = recallExpectedMap.get(b.index), btn = boxBtnMap.get(b.index);
          const val = inp?.value || '';
          st.recallValues[b.index] = val;
          const verdict = MBY.helpers.evaluateRecallMatch(val, [b.label, ...(b.aliases || [])], rel.fuzzy, rel.caseSensitive);
          inp?.classList.remove('correct', 'fuzzy-ok', 'wrong');
          btn?.classList.remove('correct', 'wrong', 'revealed');
          if (verdict === 'exact') {
            inp?.classList.add('correct'); btn?.classList.add('correct'); expEl?.classList.add('hidden');
          } else if (verdict === 'fuzzy') {
            anyFuzzy = true; inp?.classList.add('fuzzy-ok'); btn?.classList.add('correct');
            if (expEl) { expEl.textContent = 'Accepted: ' + b.label; expEl.classList.remove('hidden'); }
          } else {
            allOk = false; inp?.classList.add('wrong'); btn?.classList.add('wrong');
            if (expEl) { expEl.textContent = 'Answer: ' + b.label; expEl.classList.remove('hidden'); }
          }
        });
        st.checked = true; st.isCorrect = allOk;
        unlockZoom();
        MBY.helpers.recordGradeAttempt(m, allOk);
        MBY.helpers.setFeedbackMessage(feedback, allOk ? (anyFuzzy ? 'Almost Correct!' : 'All labels correct!') : 'Review highlighted labels.', allOk);
        overrideBtn.classList.toggle('hidden', allOk);
        retryBtn.classList.remove('hidden');
        if (explanationBox) explanationBox.classList.remove('hidden');
        if (onAnswered) onAnswered();
        requestAnimationFrame(ctx.updateScrollDownHint);
      });

      overrideBtn.addEventListener('click', () => {
        st.isCorrect = true;
        boxes.forEach(b => {
          recallInputMap.get(b.index)?.classList.remove('wrong');
          recallInputMap.get(b.index)?.classList.add('correct');
          boxBtnMap.get(b.index)?.classList.remove('wrong');
          boxBtnMap.get(b.index)?.classList.add('correct');
        });
        if (m) m.isCorrect = true;
        feedback.textContent = 'Marked correct (self-override).';
        feedback.style.color = 'var(--ok)';
        overrideBtn.classList.add('hidden');
        retryBtn.classList.remove('hidden');
        if (onAnswered) onAnswered();
      });

      layout.appendChild(recallPanel);
      actionRow = mk('div', 'mby-action-row', null, [checkBtn, overrideBtn, retryBtn, feedback]);
    }

    syncBoxVisuals();
    card.appendChild(mk('div', 'mby-card-body', null, [layout, actionRow, explanationBox]));
    return card;
  }

  MBY.gradedKinds['relabel'] = true;
  MBY.kindLabels['relabel'] = 'Relabel the Image';
  MBY.questions['relabel'] = buildRelabelCard;
  MBY.search['relabel'] = (q, parts, ctx) => {
    if (!q.relabel) return;
    if (q.relabel.promptHtml) parts.push(ctx.htmlToPlainText(q.relabel.promptHtml));
    (q.relabel.boxes || []).forEach(b => { parts.push(b.label); if (b.aliases) parts.push(b.aliases.join(' ')); });
    if (q.relabel.explanationHtml) parts.push(ctx.htmlToPlainText(q.relabel.explanationHtml));
  };
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    public static IReadOnlyList<WebComponentModule> Modules { get; } =
    [
        new("recall-utils", "questions", f => f.HasRecallMatching, null, RecallUtilsJs),
        new("blank-input", "questions", f => f.HasBlankInput, BlankInputCss, null),
        new("no-error", "questions", f => f.HasNoErrorToggle, NoErrorCss, null),
        new("fill-blank", "questions", f => f.HasFillBlank, FillBlankCss, FillBlankJs),
        new("spot-error", "questions", f => f.HasSpotError, SpotErrorCss, SpotErrorJs),
        new("diff", "questions", f => f.HasDiff, DiffCss, DiffJs),
        new("relabel", "questions", f => f.HasRelabel, RelabelCss, RelabelJs)
    ];
}
