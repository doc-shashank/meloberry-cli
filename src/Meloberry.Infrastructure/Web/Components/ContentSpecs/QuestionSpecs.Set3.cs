namespace Meloberry.Infrastructure.Web.Components.ContentSpecs;

internal static class QuestionSpecsSet3
{
    private const string MechanismCss = """
.mby-mech-layout { display: flex; flex-direction: column; gap: 12px; margin-bottom: 12px; }
.mby-mech-title { font-size: var(--prompt-font-size, 22px); font-weight: 700; line-height: 1.4; }
.mby-mech-section-label { font-size: 11px; font-weight: 800; letter-spacing: 0.04em; text-transform: uppercase; color: var(--muted); }
.mby-mech-bank { display: flex; flex-wrap: wrap; gap: 8px; padding: 12px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); min-height: 52px; }
.mby-mech-chip { padding: 8px 13px; border-radius: 10px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 14px; font-weight: 600; text-align: left; cursor: pointer; transition: border-color 140ms ease, background 140ms ease; }
.mby-mech-chip:hover { border-color: var(--select); background: var(--select-soft); }
.mby-mech-seq { display: flex; flex-direction: column; gap: 8px; padding: 12px; border-radius: 12px; border: 1.5px dashed var(--border); background: var(--bg); min-height: 68px; }
.mby-mech-empty { font-size: 13px; color: var(--muted); padding: 8px 4px; text-align: center; }
.mby-mech-slot { display: flex; align-items: center; gap: 10px; padding: 10px 12px; border-radius: 10px; border: 1.5px solid var(--border); background: var(--surface); will-change: transform; }
.mby-mech-slot.is-moving { border-color: var(--select); box-shadow: 0 6px 18px rgba(15, 10, 20, 0.14); z-index: 2; }
.mby-mech-slot.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.12); }
.mby-mech-slot.wrong, .mby-mech-slot.distractor-wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.12); }
.mby-mech-slot-text { flex: 1; min-width: 0; line-height: 1.45; }
.mby-mech-slot-actions { display: flex; gap: 4px; flex-shrink: 0; }
.mby-mech-ctrl-btn { width: 28px; height: 28px; border-radius: 7px; border: 1px solid var(--border); background: var(--bg); color: var(--text); font-weight: 700; cursor: pointer; display: inline-flex; align-items: center; justify-content: center; }
.mby-mech-ctrl-btn:hover:not(:disabled) { border-color: var(--select); color: var(--select); }
.mby-mech-ctrl-btn:disabled { opacity: 0.35; cursor: not-allowed; }
.mby-mech-canonical { margin-top: 10px; padding: 12px 14px; border-radius: 12px; border: 1px solid var(--border); border-left: 4px solid var(--ok); background: var(--bg); font-size: 0.94em; }
.mby-mech-canonical ol { margin: 6px 0 0; padding-left: 20px; }
""";

    private const string MechanismJs = """
(function (MBY) {
  function buildMechanismCard(slide, ctx, onAnswered) {
    const m = ctx.metricById.get(slide.id), mech = slide.mechanism;
    const card = mk('div', 'mby-card', null, [createUpperTab('MECHANISM ARRANGEMENT')]);
    applySlideLayoutVars(card, resolveSlideLayout(slide, 'mcq'));

    if (!ctx.gradedStateMap.has(slide.id)) {
      const shuffled = MBY.helpers.deterministicShuffle(mech.steps || [], `${slide.id}:mech`, s => String(s.stepId));
      ctx.gradedStateMap.set(slide.id, { bankIds: shuffled.map(s => s.stepId), placedIds: [], checked: false, isCorrect: null });
    }
    const st = ctx.gradedStateMap.get(slide.id);
    const stepById = new Map((mech.steps || []).map(s => [s.stepId, s]));

    const layout = mk('div', 'mby-mech-layout');
    if (mech.titleHtml) {
      const titleEl = mk('div', 'mby-mech-title');
      titleEl.innerHTML = mech.titleHtml; ctx.renderMathIn(titleEl);
      layout.appendChild(titleEl);
    }

    const bankEl = mk('div', 'mby-mech-bank');
    const seqEl = mk('div', 'mby-mech-seq');
    const canonicalBox = mk('div', 'mby-mech-canonical hidden');
    const checkBtn = mk('button', 'mby-check-btn', 'Check Sequence'); checkBtn.type = 'button';
    const feedback = mk('span');
    const explanationBox = MBY.helpers.createExplanationBox(mech.explanationHtml, st.checked, ctx);

    const animateSeqUpdate = mutateFn => {
      const firstRects = new Map();
      seqEl.querySelectorAll('.mby-mech-slot[data-step-id]').forEach(el => {
        firstRects.set(el.dataset.stepId, el.getBoundingClientRect());
      });
      mutateFn();
      renderZones();
      requestAnimationFrame(() => {
        seqEl.querySelectorAll('.mby-mech-slot[data-step-id]').forEach(el => {
          const first = firstRects.get(el.dataset.stepId);
          if (!first) return;
          const last = el.getBoundingClientRect();
          const dy = first.top - last.top;
          if (Math.abs(dy) < 1) return;
          el.style.transition = 'none';
          el.style.transform = `translateY(${dy}px)`;
          el.classList.add('is-moving');
          el.getBoundingClientRect();
          el.style.transition = 'transform 220ms cubic-bezier(0.22, 1, 0.36, 1)';
          el.style.transform = 'translateY(0)';
          setTimeout(() => {
            el.style.transition = '';
            el.style.transform = '';
            el.classList.remove('is-moving');
          }, 240);
        });
        ctx.updateScrollDownHint();
      });
    };

    MBY.helpers.bindDropTarget(bankEl, 'application/x-mby-mech-step', e => {
      try {
        const payload = JSON.parse(e.dataTransfer?.getData('application/x-mby-mech-step') || 'null');
        if (payload && payload.source === 'seq' && st.placedIds.includes(payload.sid)) {
          animateSeqUpdate(() => {
            st.placedIds = st.placedIds.filter(x => x !== payload.sid);
            if (!st.bankIds.includes(payload.sid)) st.bankIds.push(payload.sid);
          });
        }
      } catch {}
    }, true);

    seqEl.addEventListener('dragover', e => {
      if (e.dataTransfer?.types.includes('application/x-mby-mech-step')) {
        e.preventDefault();
        seqEl.classList.add('mby-drop-over');
      }
    });
    seqEl.addEventListener('dragleave', e => { if (!seqEl.contains(e.relatedTarget)) seqEl.classList.remove('mby-drop-over'); });
    seqEl.addEventListener('drop', e => {
      if (e.target.closest('.mby-mech-slot')) return;
      e.preventDefault();
      seqEl.classList.remove('mby-drop-over');
      try {
        const payload = JSON.parse(e.dataTransfer?.getData('application/x-mby-mech-step') || 'null');
        if (!payload) return;
        animateSeqUpdate(() => {
          if (payload.source === 'bank') {
            st.bankIds = st.bankIds.filter(x => x !== payload.sid);
            if (!st.placedIds.includes(payload.sid)) st.placedIds.push(payload.sid);
          } else if (payload.source === 'seq') {
            st.placedIds = st.placedIds.filter(x => x !== payload.sid);
            st.placedIds.push(payload.sid);
          }
        });
      } catch {}
    });

    const bankEmptyEl = mk('div', 'mby-mech-empty', 'All steps placed in sequence.');
    const seqEmptyEl = mk('div', 'mby-mech-empty', 'Click or drag steps above in causal order (exclude distractors).');
    const bankChipMap = new Map();
    const slotEntryMap = new Map();

    (mech.steps || []).forEach(s => {
      const sid = s.stepId;
      const chip = mk('button', 'mby-mech-chip mby-draggable'); chip.type = 'button';
      chip.draggable = true;
      chip.innerHTML = s.html; ctx.renderMathIn(chip);
      chip.addEventListener('dragstart', e => {
        if (e.dataTransfer) {
          e.dataTransfer.setData('application/x-mby-mech-step', JSON.stringify({ source: 'bank', sid }));
          e.dataTransfer.effectAllowed = 'move';
        }
        chip.classList.add('is-dragging');
      });
      chip.addEventListener('dragend', () => chip.classList.remove('is-dragging'));
      chip.addEventListener('click', () => {
        animateSeqUpdate(() => {
          st.bankIds = st.bankIds.filter(x => x !== sid);
          st.placedIds.push(sid);
        });
      });
      bankChipMap.set(sid, chip);

      const slot = mk('div', 'mby-mech-slot mby-draggable');
      slot.draggable = true;
      slot.dataset.stepId = String(sid);
      slot.addEventListener('dragstart', e => {
        if (e.dataTransfer) {
          e.dataTransfer.setData('application/x-mby-mech-step', JSON.stringify({ source: 'seq', sid }));
          e.dataTransfer.effectAllowed = 'move';
        }
        slot.classList.add('is-dragging');
      });
      slot.addEventListener('dragend', () => slot.classList.remove('is-dragging'));
      slot.addEventListener('dragover', e => {
        if (e.dataTransfer?.types.includes('application/x-mby-mech-step')) {
          e.preventDefault();
          e.stopPropagation();
          slot.classList.add('mby-drop-over');
        }
      });
      slot.addEventListener('dragleave', () => slot.classList.remove('mby-drop-over'));
      slot.addEventListener('drop', e => {
        e.preventDefault();
        e.stopPropagation();
        slot.classList.remove('mby-drop-over');
        seqEl.classList.remove('mby-drop-over');
        try {
          const payload = JSON.parse(e.dataTransfer?.getData('application/x-mby-mech-step') || 'null');
          if (!payload || payload.sid === sid) return;
          const rect = slot.getBoundingClientRect();
          const insertAfter = e.clientY > rect.top + (rect.height / 2);
          animateSeqUpdate(() => {
            st.bankIds = st.bankIds.filter(x => x !== payload.sid);
            st.placedIds = st.placedIds.filter(x => x !== payload.sid);
            const targetIdx = st.placedIds.indexOf(sid);
            const destIdx = targetIdx < 0 ? st.placedIds.length : (insertAfter ? targetIdx + 1 : targetIdx);
            st.placedIds.splice(destIdx, 0, payload.sid);
          });
        } catch {}
      });
      const badge = mk('span', 'mby-mech-badge', '1');
      const txt = mk('div', 'mby-mech-slot-text'); txt.innerHTML = s.html; ctx.renderMathIn(txt);
      const upBtn = mk('button', 'mby-mech-ctrl-btn', '↑'); upBtn.type = 'button'; upBtn.title = 'Move step up';
      upBtn.addEventListener('click', () => {
        const idx = st.placedIds.indexOf(sid);
        if (idx <= 0) return;
        animateSeqUpdate(() => {
          const tmp = st.placedIds[idx - 1]; st.placedIds[idx - 1] = st.placedIds[idx]; st.placedIds[idx] = tmp;
        });
      });
      const dnBtn = mk('button', 'mby-mech-ctrl-btn', '↓'); dnBtn.type = 'button'; dnBtn.title = 'Move step down';
      dnBtn.addEventListener('click', () => {
        const idx = st.placedIds.indexOf(sid);
        if (idx < 0 || idx >= st.placedIds.length - 1) return;
        animateSeqUpdate(() => {
          const tmp = st.placedIds[idx + 1]; st.placedIds[idx + 1] = st.placedIds[idx]; st.placedIds[idx] = tmp;
        });
      });
      const rmBtn = mk('button', 'mby-mech-ctrl-btn', '✕'); rmBtn.type = 'button'; rmBtn.title = 'Return step to bank';
      rmBtn.addEventListener('click', () => {
        animateSeqUpdate(() => {
          st.placedIds = st.placedIds.filter(x => x !== sid);
          st.bankIds.push(sid);
        });
      });
      slot.appendChild(badge); slot.appendChild(txt);
      slot.appendChild(mk('div', 'mby-mech-slot-actions', null, [upBtn, dnBtn, rmBtn]));
      slotEntryMap.set(sid, { slot, badge, upBtn, dnBtn });
    });

    const renderZones = () => {
      if (st.bankIds.length === 0) {
        bankEl.replaceChildren(bankEmptyEl);
      } else {
        bankEl.replaceChildren(...st.bankIds.map(sid => bankChipMap.get(sid)).filter(Boolean));
      }

      if (st.placedIds.length === 0) {
        seqEl.replaceChildren(seqEmptyEl);
      } else {
        const nodes = [];
        st.placedIds.forEach((sid, idx) => {
          const entry = slotEntryMap.get(sid); if (!entry) return;
          entry.badge.textContent = String(idx + 1);
          entry.upBtn.disabled = idx === 0;
          entry.dnBtn.disabled = idx === st.placedIds.length - 1;
          nodes.push(entry.slot);
        });
        seqEl.replaceChildren(...nodes);
      }
    };

    renderZones();
    layout.appendChild(mk('div', 'mby-mech-section-label', 'Available Steps'));
    layout.appendChild(bankEl);
    layout.appendChild(mk('div', 'mby-mech-section-label', 'Your Sequence'));
    layout.appendChild(seqEl);
    layout.appendChild(canonicalBox);

    const retryBtn = MBY.helpers.createRetryBtn(() => {
      const reshuffled = MBY.helpers.deterministicShuffle(mech.steps || [], `${slide.id}:mech`, s => String(s.stepId));
      st.bankIds = reshuffled.map(s => s.stepId);
      st.placedIds = [];
      st.checked = false;
      st.isCorrect = null;
      canonicalBox.innerHTML = '';
      canonicalBox.classList.add('hidden');
      slotEntryMap.forEach(({ slot }) => slot.classList.remove('correct', 'wrong', 'distractor-wrong'));
      feedback.textContent = '';
      if (explanationBox) explanationBox.classList.add('hidden');
      retryBtn.classList.add('hidden');
      renderZones();
      if (m) m.isCorrect = null;
      if (onAnswered) onAnswered();
      requestAnimationFrame(ctx.updateScrollDownHint);
    }, st.checked);

    checkBtn.addEventListener('click', () => {
      const canonicalSteps = (mech.steps || []).filter(s => !s.isDistractor).slice().sort((a, b) => a.orderGroup - b.orderGroup);
      const expectedGroups = canonicalSteps.map(s => s.orderGroup);
      const placedSteps = st.placedIds.map(id => stepById.get(id)).filter(Boolean);
      const slotEls = Array.from(seqEl.querySelectorAll('.mby-mech-slot'));
      let allOk = placedSteps.length === expectedGroups.length;

      placedSteps.forEach((s, idx) => {
        const slot = slotEls[idx];
        slot?.classList.remove('correct', 'wrong', 'distractor-wrong');
        if (s.isDistractor) {
          slot?.classList.add('distractor-wrong');
          allOk = false;
        } else if (idx < expectedGroups.length && s.orderGroup === expectedGroups[idx]) {
          slot?.classList.add('correct');
        } else {
          slot?.classList.add('wrong');
          allOk = false;
        }
      });

      if (!allOk) {
        canonicalBox.innerHTML = '<strong>Canonical Order:</strong>';
        const ol = mk('ol');
        canonicalSteps.forEach(cs => { const li = mk('li'); li.innerHTML = cs.html; ctx.renderMathIn(li); ol.appendChild(li); });
        canonicalBox.appendChild(ol);
        canonicalBox.classList.remove('hidden');
      } else {
        canonicalBox.classList.add('hidden');
      }

      st.checked = true; st.isCorrect = allOk;
      MBY.helpers.recordGradeAttempt(m, allOk);
      MBY.helpers.setFeedbackMessage(feedback, allOk ? 'Sequence correct!' : 'Review highlighted steps.', allOk);
      retryBtn.classList.remove('hidden');
      if (explanationBox) explanationBox.classList.remove('hidden');
      if (onAnswered) onAnswered();
      requestAnimationFrame(ctx.updateScrollDownHint);
    });

    card.appendChild(mk('div', 'mby-card-body', null, [layout, mk('div', 'mby-action-row', null, [checkBtn, retryBtn, feedback]), explanationBox]));
    return card;
  }

  MBY.gradedKinds['mechanism'] = true;
  MBY.kindLabels['mechanism'] = 'Mechanism';
  MBY.questions['mechanism'] = buildMechanismCard;
  MBY.search['mechanism'] = (q, parts, ctx) => {
    if (!q.mechanism) return;
    if (q.mechanism.titleHtml) parts.push(ctx.htmlToPlainText(q.mechanism.titleHtml));
    (q.mechanism.steps || []).forEach(s => parts.push(ctx.htmlToPlainText(s.html)));
    if (q.mechanism.explanationHtml) parts.push(ctx.htmlToPlainText(q.mechanism.explanationHtml));
  };
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    private const string ClassifyCss = """
.mby-classify-wrap { display: flex; flex-direction: column; gap: 10px; }
.mby-classify-pool { display: flex; flex-wrap: wrap; gap: 8px; min-height: 44px; padding: 10px 12px; border-radius: 12px; border: 1.5px dashed var(--border); background: var(--bg); align-items: center; }
.mby-classify-buckets { display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: 10px; }
.mby-classify-bucket { display: flex; flex-direction: column; gap: 8px; min-height: 96px; padding: 10px 12px; border-radius: 12px; border: 1.5px solid var(--border); background: var(--bg); cursor: pointer; }
.mby-classify-bucket.target-ready { border-color: var(--select); background: var(--select-soft); }
.mby-classify-bucket-title { font-size: 13px; font-weight: 800; color: var(--select); text-transform: uppercase; letter-spacing: 0.03em; }
.mby-classify-bucket-items { display: flex; flex-wrap: wrap; gap: 6px; }
.mby-classify-chip { padding: 6px 11px; border-radius: 999px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 13.5px; font-weight: 600; cursor: pointer; display: inline-flex; align-items: center; gap: 6px; max-width: 100%; }
.mby-classify-chip.selected { border-color: var(--select); background: var(--select-soft); }
.mby-classify-chip.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.14); }
.mby-classify-chip.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.14); }
.mby-classify-fix { font-size: 11px; font-weight: 700; color: var(--ok); }
""";

    private const string ClassifyJs = """
(function (MBY) {
  function buildClassifyCard(slide, ctx, onAnswered) {
    const cl = slide.classify;
    const card = mk('div', 'mby-card', null, [createUpperTab('CLASSIFICATION')]); applySlideLayoutVars(card, resolveSlideLayout(slide, 'mcq'));
    const st = ctx.gradedStateMap.get(slide.id) || {
      placements: {},
      selectedItemId: null,
      checked: false,
      isCorrect: null,
      order: cl.items.map(it => it.itemId).sort(() => Math.random() - 0.5)
    };
    ctx.gradedStateMap.set(slide.id, st);

    const itemsById = new Map(cl.items.map(it => [it.itemId, it]));
    const bucketNameById = new Map(cl.buckets.map(b => [b.bucketId, b.name]));

    const body = mk('div', 'mby-card-body');
    if (cl.titleHtml || cl.title) {
      const titleEl = mk('div', 'mby-diff-title');
      if (cl.titleHtml) { titleEl.innerHTML = cl.titleHtml; ctx.renderMathIn(titleEl); }
      else titleEl.textContent = cl.title;
      body.appendChild(titleEl);
    }

    const poolEl = mk('div', 'mby-classify-pool');
    const bucketsEl = mk('div', 'mby-classify-buckets');
    body.appendChild(poolEl);
    body.appendChild(bucketsEl);

    const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
    const feedback = mk('span');
    const explanationBox = MBY.helpers.createExplanationBox(cl.explanationHtml, st.checked, ctx);

    function assignItem(itemId, bucketId) {
      if (bucketId === null || bucketId === undefined) delete st.placements[itemId];
      else st.placements[itemId] = bucketId;
      st.selectedItemId = null;
      st.checked = false;
      st.isCorrect = null;
      feedback.textContent = '';
      if (explanationBox) explanationBox.classList.add('hidden');
      renderClassifyUi();
    }

    const retryBtn = MBY.helpers.createRetryBtn(() => {
      st.placements = {};
      st.selectedItemId = null;
      st.checked = false;
      st.isCorrect = null;
      feedback.textContent = '';
      if (explanationBox) explanationBox.classList.add('hidden');
      retryBtn.classList.add('hidden');
      renderClassifyUi();
      const m = ctx.metricById.get(slide.id); if (m) m.isCorrect = null;
      if (onAnswered) onAnswered();
    }, st.checked);

    const poolEmptyEl = mk('span', 'mby-diff-subnote', 'All items sorted — click a chip or bucket to adjust.');
    const chipEntryMap = new Map();
    cl.items.forEach(it => {
      const chip = mk('button', 'mby-classify-chip');
      chip.type = 'button';
      chip.draggable = true;
      const span = mk('span');
      span.innerHTML = it.html;
      ctx.renderMathIn(span);
      chip.appendChild(span);
      const fixSpan = mk('span', 'mby-classify-fix hidden');
      chip.appendChild(fixSpan);
      chip.addEventListener('dragstart', e => {
        if (e.dataTransfer) { e.dataTransfer.setData('application/x-mby-classify-item', String(it.itemId)); e.dataTransfer.effectAllowed = 'move'; }
      });
      chip.addEventListener('click', e => {
        e.stopPropagation();
        st.selectedItemId = st.selectedItemId === it.itemId ? null : it.itemId;
        renderClassifyUi();
      });
      chipEntryMap.set(it.itemId, { item: it, chip, fixSpan });
    });

    const bucketEntries = cl.buckets.map(b => {
      const box = mk('div', 'mby-classify-bucket');
      const bTitle = mk('div', 'mby-classify-bucket-title');
      bTitle.innerHTML = b.nameHtml || b.name;
      ctx.renderMathIn(bTitle);
      box.appendChild(bTitle);
      const itemsRow = mk('div', 'mby-classify-bucket-items');
      box.appendChild(itemsRow);
      MBY.helpers.bindDropTarget(box, 'application/x-mby-classify-item', e => {
        const raw = e.dataTransfer?.getData('application/x-mby-classify-item');
        if (raw !== undefined && raw !== '') assignItem(Number(raw), b.bucketId);
      });
      box.addEventListener('click', () => { if (st.selectedItemId !== null) assignItem(st.selectedItemId, b.bucketId); });
      bucketsEl.appendChild(box);
      return { bucket: b, box, itemsRow };
    });

    MBY.helpers.bindDropTarget(poolEl, 'application/x-mby-classify-item', e => {
      const raw = e.dataTransfer?.getData('application/x-mby-classify-item');
      if (raw !== undefined && raw !== '') assignItem(Number(raw), null);
    });
    poolEl.addEventListener('click', () => { if (st.selectedItemId !== null) assignItem(st.selectedItemId, null); });

    function renderClassifyUi() {
      chipEntryMap.forEach(({ item, chip, fixSpan }) => {
        chip.classList.toggle('selected', st.selectedItemId === item.itemId);
        chip.classList.remove('correct', 'wrong');
        fixSpan.classList.add('hidden');
        if (st.checked) {
          const ok = st.placements[item.itemId] === item.bucketId;
          chip.classList.add(ok ? 'correct' : 'wrong');
          if (!ok) {
            fixSpan.textContent = '→ ' + (bucketNameById.get(item.bucketId) || '');
            fixSpan.classList.remove('hidden');
          }
        }
      });

      const unplacedChips = st.order.filter(id => st.placements[id] === undefined).map(id => chipEntryMap.get(id)?.chip).filter(Boolean);
      if (unplacedChips.length === 0) poolEl.replaceChildren(poolEmptyEl);
      else poolEl.replaceChildren(...unplacedChips);

      bucketEntries.forEach(({ bucket, box, itemsRow }) => {
        box.classList.toggle('target-ready', st.selectedItemId !== null);
        const placedChips = st.order.filter(id => st.placements[id] === bucket.bucketId).map(id => chipEntryMap.get(id)?.chip).filter(Boolean);
        itemsRow.replaceChildren(...placedChips);
      });
      retryBtn.classList.toggle('hidden', !st.checked);
    }

    checkBtn.addEventListener('click', () => {
      const placedCount = cl.items.filter(it => st.placements[it.itemId] !== undefined).length;
      const ok = placedCount === cl.items.length && cl.items.every(it => st.placements[it.itemId] === it.bucketId);
      st.checked = true;
      st.isCorrect = ok;
      MBY.helpers.recordGradeAttempt(ctx.metricById.get(slide.id), ok);
      MBY.helpers.setFeedbackMessage(feedback, ok ? `Correct! All ${cl.items.length} items sorted.` : `${cl.items.filter(it => st.placements[it.itemId] === it.bucketId).length} / ${cl.items.length} correct.`, ok);
      if (explanationBox) explanationBox.classList.remove('hidden');
      renderClassifyUi();
      if (onAnswered) onAnswered();
    });

    renderClassifyUi();
    const actionRow = mk('div', 'mby-action-row', null, [checkBtn, retryBtn, feedback]);
    body.appendChild(actionRow);
    if (explanationBox) body.appendChild(explanationBox);
    card.appendChild(body);
    return card;
  }

  MBY.gradedKinds['classify'] = true;
  MBY.kindLabels['classify'] = 'Classification';
  MBY.questions['classify'] = buildClassifyCard;
  MBY.search['classify'] = (q, parts, ctx) => {
    if (!q.classify) return;
    if (q.classify.titleHtml) parts.push(ctx.htmlToPlainText(q.classify.titleHtml));
    (q.classify.buckets || []).forEach(b => parts.push(b.name));
    (q.classify.items || []).forEach(it => parts.push(ctx.htmlToPlainText(it.html)));
    if (q.classify.explanationHtml) parts.push(ctx.htmlToPlainText(q.classify.explanationHtml));
  };
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    private const string MatchCss = """
.mby-match-wrap { display: flex; flex-direction: column; gap: 10px; }
.mby-match-rows { display: flex; flex-direction: column; gap: 8px; }
.mby-match-row { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1.2fr); gap: 8px; align-items: stretch; }
@media (max-width: 560px) { .mby-match-row { grid-template-columns: 1fr; } }
.mby-match-left { padding: 9px 12px; border-radius: 10px; border: 1px solid var(--border); background: var(--bg); font-weight: 650; font-size: 14px; display: flex; align-items: center; }
.mby-match-slot { padding: 8px 12px; border-radius: 10px; border: 1.5px dashed var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 14px; display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 6px; cursor: pointer; text-align: left; }
.mby-match-slot.filled { border-style: solid; }
.mby-match-slot.target-ready { border-color: var(--select); background: var(--select-soft); }
.mby-match-slot.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.14); }
.mby-match-slot.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.14); }
.mby-match-bank { display: flex; flex-wrap: wrap; gap: 8px; padding: 10px 12px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); }
.mby-match-chip { padding: 6px 12px; border-radius: 999px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 13.5px; font-weight: 600; cursor: pointer; }
.mby-match-chip.selected { border-color: var(--select); background: var(--select-soft); }
.mby-match-chip.used { opacity: 0.45; border-style: dashed; }
""";

    private const string MatchJs = """
(function (MBY) {
  function buildMatchCard(slide, ctx, onAnswered) {
    const mt = slide.match;
    const card = mk('div', 'mby-card', null, [createUpperTab('MATCHING')]); applySlideLayoutVars(card, resolveSlideLayout(slide, 'mcq'));
    const st = ctx.gradedStateMap.get(slide.id) || {
      assignments: {},
      selectedOptId: null,
      checked: false,
      isCorrect: null,
      poolOrder: mt.rightPool.map(o => o.optionId).sort(() => Math.random() - 0.5)
    };
    ctx.gradedStateMap.set(slide.id, st);

    const optById = new Map(mt.rightPool.map(o => [o.optionId, o]));
    const body = mk('div', 'mby-card-body');
    if (mt.titleHtml || mt.title) {
      const tEl = mk('div', 'mby-diff-title');
      if (mt.titleHtml) { tEl.innerHTML = mt.titleHtml; ctx.renderMathIn(tEl); }
      else tEl.textContent = mt.title;
      body.appendChild(tEl);
    }

    const wrap = mk('div', 'mby-match-wrap');
    const rowsEl = mk('div', 'mby-match-rows');
    const bankEl = mk('div', 'mby-match-bank');
    wrap.appendChild(rowsEl);
    wrap.appendChild(bankEl);
    body.appendChild(wrap);

    const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
    const feedback = mk('span');
    const explanationBox = MBY.helpers.createExplanationBox(mt.explanationHtml, st.checked, ctx);

    function assignOption(pairId, optId) {
      if (optId !== null && optId !== undefined) {
        for (const [k, v] of Object.entries(st.assignments)) {
          if (v === optId) delete st.assignments[k];
        }
        st.assignments[pairId] = optId;
      } else {
        delete st.assignments[pairId];
      }
      st.selectedOptId = null;
      st.checked = false;
      st.isCorrect = null;
      feedback.textContent = '';
      if (explanationBox) explanationBox.classList.add('hidden');
      renderMatchUi();
    }

    const retryBtn = MBY.helpers.createRetryBtn(() => {
      st.assignments = {};
      st.selectedOptId = null;
      st.checked = false;
      st.isCorrect = null;
      feedback.textContent = '';
      if (explanationBox) explanationBox.classList.add('hidden');
      retryBtn.classList.add('hidden');
      renderMatchUi();
      const m = ctx.metricById.get(slide.id); if (m) m.isCorrect = null;
      if (onAnswered) onAnswered();
    }, st.checked);

    const pairEntries = mt.pairs.map(pair => {
      const row = mk('div', 'mby-match-row');
      const left = mk('div', 'mby-match-left');
      left.innerHTML = pair.leftHtml;
      ctx.renderMathIn(left);
      row.appendChild(left);

      const slot = mk('button', 'mby-match-slot');
      slot.type = 'button';
      const valSpan = mk('span');
      const clrSpan = mk('span', 'mby-diff-subnote', '✕');
      clrSpan.addEventListener('click', e => { e.stopPropagation(); assignOption(pair.pairId, null); });
      const phSpan = mk('span', 'mby-diff-subnote', '(Click or drop match)');
      const fixSpan = mk('span', 'mby-diff-fix-badge hidden');
      fixSpan.innerHTML = '→ ' + pair.rightHtml;
      ctx.renderMathIn(fixSpan);

      MBY.helpers.bindDropTarget(slot, 'application/x-mby-match-opt', e => {
        const raw = e.dataTransfer?.getData('application/x-mby-match-opt');
        if (raw !== undefined && raw !== '') assignOption(pair.pairId, Number(raw));
      });
      slot.addEventListener('click', () => {
        if (st.selectedOptId !== null) assignOption(pair.pairId, st.selectedOptId);
        else if (st.assignments[pair.pairId] !== undefined) assignOption(pair.pairId, null);
      });
      row.appendChild(slot);
      rowsEl.appendChild(row);
      return { pair, slot, valSpan, clrSpan, phSpan, fixSpan };
    });

    const chipNodeMap = new Map();
    st.poolOrder.map(id => optById.get(id)).filter(Boolean).forEach(opt => {
      const chip = mk('button', 'mby-match-chip');
      chip.type = 'button';
      chip.draggable = true;
      chip.innerHTML = opt.html;
      ctx.renderMathIn(chip);
      chip.addEventListener('dragstart', e => {
        if (e.dataTransfer) { e.dataTransfer.setData('application/x-mby-match-opt', String(opt.optionId)); e.dataTransfer.effectAllowed = 'move'; }
      });
      chip.addEventListener('click', () => {
        st.selectedOptId = st.selectedOptId === opt.optionId ? null : opt.optionId;
        renderMatchUi();
      });
      chipNodeMap.set(opt.optionId, chip);
      bankEl.appendChild(chip);
    });

    function renderMatchUi() {
      pairEntries.forEach(({ pair, slot, valSpan, clrSpan, phSpan, fixSpan }) => {
        const optId = st.assignments[pair.pairId];
        const assigned = optId !== undefined ? optById.get(optId) : null;
        slot.classList.toggle('filled', !!assigned);
        slot.classList.toggle('target-ready', st.selectedOptId !== null);
        slot.classList.remove('correct', 'wrong');
        const slotNodes = [];
        if (assigned) {
          if (valSpan.dataset.optId !== String(optId)) {
            valSpan.dataset.optId = String(optId);
            valSpan.innerHTML = assigned.html;
            ctx.renderMathIn(valSpan);
          }
          slotNodes.push(valSpan, clrSpan);
        } else {
          delete valSpan.dataset.optId;
          slotNodes.push(phSpan);
        }
        if (st.checked) {
          const ok = !!(assigned && assigned.correctPairId === pair.pairId);
          slot.classList.add(ok ? 'correct' : 'wrong');
          fixSpan.classList.toggle('hidden', ok);
          if (!ok) slotNodes.push(fixSpan);
        } else {
          fixSpan.classList.add('hidden');
        }
        slot.replaceChildren(...slotNodes);
      });

      const usedOptIds = new Set(Object.values(st.assignments).filter(v => v !== undefined && v !== null));
      chipNodeMap.forEach((chip, optionId) => {
        chip.classList.toggle('used', usedOptIds.has(optionId));
        chip.classList.toggle('selected', st.selectedOptId === optionId);
      });
      retryBtn.classList.toggle('hidden', !st.checked);
    }

    checkBtn.addEventListener('click', () => {
      const okCount = mt.pairs.filter(p => {
        const oid = st.assignments[p.pairId];
        return oid !== undefined && optById.get(oid)?.correctPairId === p.pairId;
      }).length;
      const ok = okCount === mt.pairs.length;
      st.checked = true;
      st.isCorrect = ok;
      MBY.helpers.recordGradeAttempt(ctx.metricById.get(slide.id), ok);
      MBY.helpers.setFeedbackMessage(feedback, ok ? 'Correct!' : `${okCount} / ${mt.pairs.length} pairs correct.`, ok);
      if (explanationBox) explanationBox.classList.remove('hidden');
      renderMatchUi();
      if (onAnswered) onAnswered();
    });

    renderMatchUi();
    body.appendChild(mk('div', 'mby-action-row', null, [checkBtn, retryBtn, feedback]));
    if (explanationBox) body.appendChild(explanationBox);
    card.appendChild(body);
    return card;
  }

  MBY.gradedKinds['match'] = true;
  MBY.kindLabels['match'] = 'Matching';
  MBY.questions['match'] = buildMatchCard;
  MBY.search['match'] = (q, parts, ctx) => {
    if (!q.match) return;
    if (q.match.titleHtml) parts.push(ctx.htmlToPlainText(q.match.titleHtml));
    (q.match.pairs || []).forEach(p => { parts.push(ctx.htmlToPlainText(p.leftHtml)); parts.push(ctx.htmlToPlainText(p.rightHtml)); });
    if (q.match.explanationHtml) parts.push(ctx.htmlToPlainText(q.match.explanationHtml));
  };
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    private const string CaseCss = """
.mby-case-layout { display: grid; grid-template-columns: minmax(0, 1fr); gap: 12px; }
@media (min-width: 900px) { .mby-case-layout.has-split { grid-template-columns: minmax(260px, 0.92fr) minmax(0, 1.08fr); align-items: start; } }
.mby-case-stem { padding: 12px 14px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); display: flex; flex-direction: column; gap: 8px; }
.mby-case-stem-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.mby-case-stem-badge { align-self: flex-start; padding: 2px 9px; border-radius: 999px; background: var(--select-soft); color: var(--select); font-size: 11px; font-weight: 800; text-transform: uppercase; letter-spacing: 0.04em; }
.mby-case-main { display: flex; flex-direction: column; gap: 10px; min-width: 0; }
.mby-case-tabs { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
.mby-case-tab { padding: 5px 11px; border-radius: 999px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 12.5px; font-weight: 700; cursor: pointer; }
.mby-case-tab.active { border-color: var(--select); background: var(--select-soft); color: var(--select); }
.mby-case-tab.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.14); color: var(--ok); }
.mby-case-tab.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.14); color: var(--bad); }
.mby-case-subhost { min-width: 0; }
.mby-case-subhost > .mby-card { padding: 12px; border-radius: 12px; box-shadow: none; }
""";

    private const string CaseJs = """
(function (MBY) {
  function buildCaseGroupCard(slide, ctx, onAnswered) {
    const cs = slide.caseGroup;
    const subQuestions = cs.questions || [];
    const card = mk('div', 'mby-card', null, [createUpperTab(cs.title ? 'CASE STUDY • ' + cs.title : 'CASE STUDY')]);
    applySlideLayoutVars(card, resolveSlideLayout(slide, 'mcq'));

    const st = ctx.gradedStateMap.get(slide.id) || { activePartIdx: 0, checked: false, isCorrect: null };
    ctx.gradedStateMap.set(slide.id, st);

    subQuestions.forEach((sq, idx) => {
      if (!ctx.metricById.has(sq.id)) {
        ctx.metricById.set(sq.id, {
          slideIndex: idx + 1, id: sq.id, kind: sq.kind, timeSpentMs: 0, visitedCount: 0,
          checkAttempts: ctx.isGradedKind(sq.kind) ? 0 : null,
          selectedOptionIndices: sq.kind === 'mcq' ? [] : null,
          correctOptionIndices: sq.kind === 'mcq' ? (sq.mcq?.options || []).filter(o => o.isCorrect).map(o => o.index) : null,
          firstTryCorrect: null, isCorrect: null, flipCount: sq.kind === 'flashcard' ? 0 : null, flashcardRating: null
        });
      }
    });

    const body = mk('div', 'mby-card-body');
    const useSplit = !!cs.imagePath || ctx.htmlToPlainText(cs.stemHtml || '').length > 160;
    const layout = mk('div', 'mby-case-layout' + (useSplit ? ' has-split' : ''));

    const stemBox = mk('div', 'mby-case-stem');
    const stemHeader = mk('div', 'mby-case-stem-head');
    stemHeader.appendChild(mk('span', 'mby-case-stem-badge', 'Case Stem'));
    const scoreBadge = mk('span', 'mby-step-counter');
    stemHeader.appendChild(scoreBadge);
    stemBox.appendChild(stemHeader);

    const stemContent = mk('div');
    stemContent.innerHTML = cs.stemHtml || '';
    ctx.renderMathIn(stemContent);
    stemBox.appendChild(stemContent);

    if (cs.imagePath) {
      const imgWrap = ctx.makeZoomImg(cs.imagePath, cs.title || 'Case figure');
      stemBox.appendChild(imgWrap);
      if (cs.captionHtml) {
        const capEl = mk('div', 'mby-figcaption');
        capEl.innerHTML = cs.captionHtml;
        ctx.renderMathIn(capEl);
        stemBox.appendChild(capEl);
      }
    }
    layout.appendChild(stemBox);

    const mainCol = mk('div', 'mby-case-main');
    const tabsRow = mk('div', 'mby-case-tabs');
    const subHost = mk('div', 'mby-case-subhost');
    const explanationBox = MBY.helpers.createExplanationBox(cs.explanationHtml, false, ctx);
    mainCol.appendChild(tabsRow);
    mainCol.appendChild(subHost);
    layout.appendChild(mainCol);
    body.appendChild(layout);
    if (explanationBox) body.appendChild(explanationBox);
    card.appendChild(body);

    function recomputeAggregate() {
      const gradedSubs = subQuestions.filter(sq => ctx.isGradedKind(sq.kind));
      const gradedMetrics = gradedSubs.map(sq => ctx.metricById.get(sq.id));
      const correctCount = gradedMetrics.filter(sm => sm?.isCorrect === true).length;
      const attemptsSum = gradedMetrics.reduce((acc, sm) => acc + (sm?.checkAttempts || 0), 0);
      const allChecked = gradedMetrics.length > 0 && gradedMetrics.every(sm => (sm?.checkAttempts || 0) > 0);
      const allCorrect = allChecked && gradedMetrics.every(sm => sm?.isCorrect === true);
      const allFirstTry = allChecked && gradedMetrics.every(sm => sm?.firstTryCorrect === true);

      scoreBadge.textContent = gradedSubs.length > 0
        ? `${correctCount} / ${gradedSubs.length} parts correct`
        : `${subQuestions.length} parts`;

      st.checked = allChecked;
      st.isCorrect = allChecked ? allCorrect : null;
      const parentMetric = ctx.metricById.get(slide.id);
      if (parentMetric) {
        parentMetric.checkAttempts = attemptsSum;
        parentMetric.isCorrect = allChecked ? allCorrect : null;
        parentMetric.firstTryCorrect = allChecked ? allFirstTry : null;
      }
      if (explanationBox && allChecked) explanationBox.classList.remove('hidden');
    }

    const partNodeCache = new Map();
    subQuestions.forEach((_, idx) => {
      const tab = mk('button', 'mby-case-tab', `Part ${idx + 1}`);
      tab.type = 'button';
      tab.addEventListener('click', () => { st.activePartIdx = idx; renderTabsAndActiveSub(); });
      tabsRow.appendChild(tab);
    });

    function renderTabsOnly() {
      Array.from(tabsRow.children).forEach((tab, idx) => {
        const sq = subQuestions[idx];
        const sm = sq ? ctx.metricById.get(sq.id) : null;
        tab.classList.toggle('active', idx === st.activePartIdx);
        tab.classList.toggle('correct', !!(sm && (sm.isCorrect === true || sm.flashcardRating)));
        tab.classList.toggle('wrong', !!(sm && sm.isCorrect === false));
      });
    }

    function renderTabsAndActiveSub() {
      recomputeAggregate();
      renderTabsOnly();
      partNodeCache.forEach(node => node.classList.add('hidden'));
      const activeIdx = Math.max(0, Math.min(st.activePartIdx, subQuestions.length - 1));
      const activeSq = subQuestions[activeIdx];
      if (activeSq) {
        let partNode = partNodeCache.get(activeIdx);
        if (!partNode) {
          partNode = ctx.renderSingleQuestionWidget(activeSq, () => {
            recomputeAggregate();
            renderTabsOnly();
            if (onAnswered) onAnswered();
          });
          partNodeCache.set(activeIdx, partNode);
          subHost.appendChild(partNode);
        }
        partNode.classList.remove('hidden');
      }
      requestAnimationFrame(ctx.updateScrollDownHint);
    }

    renderTabsAndActiveSub();
    return card;
  }

  MBY.gradedKinds['case'] = true;
  MBY.kindLabels['case'] = 'Case Study';
  MBY.questions['case'] = buildCaseGroupCard;
  MBY.search['case'] = (q, parts, ctx) => {
    if (!q.caseGroup) return;
    if (q.caseGroup.titleHtml) parts.push(ctx.htmlToPlainText(q.caseGroup.titleHtml));
    parts.push(ctx.htmlToPlainText(q.caseGroup.stemHtml));
    (q.caseGroup.questions || []).forEach(sq => MBY.search[sq.kind]?.(sq, parts, ctx));
    if (q.caseGroup.explanationHtml) parts.push(ctx.htmlToPlainText(q.caseGroup.explanationHtml));
  };
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    public static IReadOnlyList<WebComponentModule> Modules { get; } =
    [
        new("mechanism", "questions", f => f.HasMechanism, MechanismCss, MechanismJs),
        new("classify", "questions", f => f.HasClassify, ClassifyCss, ClassifyJs),
        new("match", "questions", f => f.HasMatch, MatchCss, MatchJs),
        new("case", "questions", f => f.HasCase, CaseCss, CaseJs)
    ];
}
