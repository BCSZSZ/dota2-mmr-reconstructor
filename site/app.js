const JST_OFFSET = "+09:00";
const DAY_SECONDS = 86_400;

const elements = {
  heroStatus: document.querySelector("#heroStatus"),
  updateStamp: document.querySelector("#updateStamp"),
  fromDate: document.querySelector("#fromDate"),
  toDate: document.querySelector("#toDate"),
  fromRange: document.querySelector("#fromRange"),
  toRange: document.querySelector("#toRange"),
  timelineSelection: document.querySelector("#timelineSelection"),
  minimumTime: document.querySelector("#minimumTime"),
  maximumTime: document.querySelector("#maximumTime"),
  selectedTime: document.querySelector("#selectedTime"),
  matchCount: document.querySelector("#matchCount"),
  playerCount: document.querySelector("#playerCount"),
  rangeDays: document.querySelector("#rangeDays"),
  availableCount: document.querySelector("#availableCount"),
  coverageNotice: document.querySelector("#coverageNotice"),
  statsBody: document.querySelector("#statsBody"),
  statsEmpty: document.querySelector("#statsEmpty"),
  matrixTable: document.querySelector("#matrixTable"),
  matrixEmpty: document.querySelector("#matrixEmpty"),
  matrixScroll: document.querySelector("#matrixScroll"),
  quickButtons: [...document.querySelectorAll(".quick-ranges button")],
};

const state = {
  data: null,
  minimum: 0,
  maximum: 0,
  from: 0,
  to: 0,
};

const jstFormatter = new Intl.DateTimeFormat("zh-CN", {
  timeZone: "Asia/Tokyo",
  year: "numeric",
  month: "2-digit",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
  hourCycle: "h23",
});

const shortJstFormatter = new Intl.DateTimeFormat("zh-CN", {
  timeZone: "Asia/Tokyo",
  year: "numeric",
  month: "2-digit",
  day: "2-digit",
});

function jstParts(seconds) {
  const parts = jstFormatter.formatToParts(new Date(seconds * 1000));
  return Object.fromEntries(parts.map((part) => [part.type, part.value]));
}

function formatJst(seconds) {
  const p = jstParts(seconds);
  return `${p.year}-${p.month}-${p.day} ${p.hour}:${p.minute}`;
}

function formatShortJst(seconds) {
  return shortJstFormatter.format(new Date(seconds * 1000)).replaceAll("/", "-");
}

function toDateInput(seconds) {
  const p = jstParts(seconds);
  return `${p.year}-${p.month}-${p.day}T${p.hour}:${p.minute}`;
}

function parseJstInput(value) {
  if (!value) return Number.NaN;
  return Date.parse(`${value}:00${JST_OFFSET}`) / 1000;
}

function formatPercent(value) {
  return `${(value * 100).toFixed(1)}%`;
}

function makeCell(tag, text, className = "") {
  const cell = document.createElement(tag);
  cell.textContent = text;
  if (className) cell.className = className;
  return cell;
}

function makePlayerLabel(player) {
  const wrapper = document.createDocumentFragment();
  const name = document.createTextNode(player.name);
  const account = document.createElement("small");
  account.textContent = String(player.accountId);
  wrapper.append(name, account);
  return wrapper;
}

function setQuickButtonActive(target) {
  elements.quickButtons.forEach((button) => button.classList.toggle("active", button === target));
}

function updateRangeControls() {
  elements.fromRange.value = String(state.from);
  elements.toRange.value = String(state.to);
  elements.fromDate.value = toDateInput(state.from);
  elements.toDate.value = toDateInput(state.to);
  elements.selectedTime.textContent = `${formatJst(state.from)} — ${formatJst(state.to)}`;

  const span = Math.max(1, state.maximum - state.minimum);
  const left = ((state.from - state.minimum) / span) * 100;
  const right = ((state.to - state.minimum) / span) * 100;
  elements.timelineSelection.style.left = `${Math.max(0, left)}%`;
  elements.timelineSelection.style.width = `${Math.max(0, right - left)}%`;
}

function setRange(from, to, quickButton = null) {
  state.from = Math.max(state.minimum, Math.min(from, state.maximum));
  state.to = Math.max(state.minimum, Math.min(to, state.maximum));
  if (state.from > state.to) [state.from, state.to] = [state.to, state.from];
  updateRangeControls();
  setQuickButtonActive(quickButton);
  render();
}

function getVisiblePlayers(match) {
  return (match.players || []).filter(
    (player) => Number.isFinite(player.account_id) && Number.isFinite(player.player_slot),
  );
}

function calculate(matches) {
  const stats = new Map();
  const pairs = new Map();

  for (const match of matches) {
    const players = getVisiblePlayers(match).map((player) => ({
      accountId: Number(player.account_id),
      name: player.personaname || `Account ${player.account_id}`,
      radiant: Number(player.player_slot) < 128,
    }));

    for (const player of players) {
      const won = player.radiant === match.radiant_win;
      const current = stats.get(player.accountId) || {
        accountId: player.accountId,
        name: player.name,
        wins: 0,
        losses: 0,
        total: 0,
        latestStart: 0,
      };
      current.wins += won ? 1 : 0;
      current.losses += won ? 0 : 1;
      current.total += 1;
      if (match.start_time >= current.latestStart) {
        current.name = player.name;
        current.latestStart = match.start_time;
      }
      stats.set(player.accountId, current);
    }

    for (const radiant of [true, false]) {
      const team = players.filter((player) => player.radiant === radiant);
      const teamWon = radiant === match.radiant_win;
      for (let left = 0; left < team.length; left += 1) {
        for (let right = left + 1; right < team.length; right += 1) {
          const ids = [team[left].accountId, team[right].accountId].sort((a, b) => a - b);
          const key = `${ids[0]}:${ids[1]}`;
          const current = pairs.get(key) || { games: 0, wins: 0 };
          current.games += 1;
          current.wins += teamWon ? 1 : 0;
          pairs.set(key, current);
        }
      }
    }
  }

  const players = [...stats.values()].sort(
    (left, right) =>
      right.total - left.total ||
      right.wins - left.wins ||
      left.losses - right.losses ||
      left.name.localeCompare(right.name, "zh-CN") ||
      left.accountId - right.accountId,
  );
  return { players, pairs };
}

function renderStats(players) {
  elements.statsBody.replaceChildren();
  elements.statsEmpty.hidden = players.length > 0;

  players.forEach((player, index) => {
    const row = document.createElement("tr");
    const rankCell = document.createElement("td");
    const rank = document.createElement("span");
    rank.className = "rank-badge";
    rank.textContent = String(index + 1);
    rankCell.append(rank);
    row.append(rankCell);
    row.append(makeCell("td", player.name, "player-name"));
    row.append(makeCell("td", String(player.accountId), "account-id"));
    row.append(makeCell("td", String(player.wins)));
    row.append(makeCell("td", String(player.losses)));
    row.append(makeCell("td", String(player.total)));
    const rateCell = document.createElement("td");
    const pill = document.createElement("span");
    pill.className = "rate-pill";
    pill.textContent = formatPercent(player.wins / player.total);
    rateCell.append(pill);
    row.append(rateCell);
    elements.statsBody.append(row);
  });
}

function pairClass(rate) {
  if (rate < 0.4) return "pair-loss";
  if (rate < 0.6) return "pair-even";
  return "pair-win";
}

function renderMatrix(players, pairs) {
  elements.matrixTable.replaceChildren();
  const hasPlayers = players.length > 0;
  elements.matrixScroll.hidden = !hasPlayers;
  elements.matrixEmpty.hidden = hasPlayers;
  if (!hasPlayers) return;

  const head = document.createElement("thead");
  const headRow = document.createElement("tr");
  headRow.append(makeCell("th", "玩家"));
  for (const player of players) {
    const th = document.createElement("th");
    th.append(makePlayerLabel(player));
    headRow.append(th);
  }
  head.append(headRow);

  const body = document.createElement("tbody");
  for (const rowPlayer of players) {
    const row = document.createElement("tr");
    const label = document.createElement("th");
    label.append(makePlayerLabel(rowPlayer));
    row.append(label);
    for (const columnPlayer of players) {
      if (rowPlayer.accountId === columnPlayer.accountId) {
        row.append(makeCell("td", "—", "pair-self"));
        continue;
      }
      const ids = [rowPlayer.accountId, columnPlayer.accountId].sort((a, b) => a - b);
      const pair = pairs.get(`${ids[0]}:${ids[1]}`);
      if (!pair) {
        row.append(makeCell("td", "", "pair-none"));
        continue;
      }
      const rate = pair.wins / pair.games;
      row.append(makeCell("td", `${pair.games}/${formatPercent(rate)}`, pairClass(rate)));
    }
    body.append(row);
  }

  elements.matrixTable.append(head, body);
}

function render() {
  if (!state.data) return;
  const matches = state.data.matches.filter(
    (match) => match.start_time >= state.from && match.start_time <= state.to,
  );
  const { players, pairs } = calculate(matches);
  elements.matchCount.textContent = String(matches.length);
  elements.playerCount.textContent = String(players.length);
  elements.rangeDays.textContent = String(Math.max(1, Math.ceil((state.to - state.from) / DAY_SECONDS)));
  renderStats(players);
  renderMatrix(players, pairs);
}

function showCoverageNotice(data) {
  const metadata = data.metadata || {};
  const unavailable = metadata.unavailable_matches || [];
  const anonymous = Number(metadata.anonymous_player_slots || 0);
  const incomplete = metadata.incomplete_roster_match_ids || [];
  const messages = [];
  if (unavailable.length) messages.push(`${unavailable.length} 场尚未取得公开详情`);
  if (anonymous) messages.push(`${anonymous} 个选手位为匿名`);
  if (incomplete.length) messages.push(`${incomplete.length} 场公开阵容不完整`);
  if (!messages.length) {
    elements.coverageNotice.hidden = true;
    return;
  }
  elements.coverageNotice.textContent = `覆盖提示：${messages.join("；")}。这些位置不会伪装成已知玩家。`;
  elements.coverageNotice.hidden = false;
}

function bindControls() {
  elements.fromRange.addEventListener("input", () => {
    const next = Math.min(Number(elements.fromRange.value), state.to);
    setRange(next, state.to);
  });
  elements.toRange.addEventListener("input", () => {
    const next = Math.max(Number(elements.toRange.value), state.from);
    setRange(state.from, next);
  });
  elements.fromDate.addEventListener("change", () => {
    const next = parseJstInput(elements.fromDate.value);
    if (Number.isFinite(next)) setRange(next, state.to);
  });
  elements.toDate.addEventListener("change", () => {
    const next = parseJstInput(elements.toDate.value);
    if (Number.isFinite(next)) setRange(state.from, next);
  });
  elements.quickButtons.forEach((button) => {
    button.addEventListener("click", () => {
      if (button.dataset.range === "all") {
        setRange(state.minimum, state.maximum, button);
        return;
      }
      const days = Number(button.dataset.days || 0);
      setRange(Math.max(state.minimum, state.maximum - days * DAY_SECONDS), state.maximum, button);
    });
  });
}

async function load() {
  try {
    const response = await fetch("./data/league.json", { cache: "no-store" });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const data = await response.json();
    if (!Array.isArray(data.matches) || data.matches.length === 0) {
      throw new Error("数据中还没有可用比赛");
    }
    state.data = data;
    state.minimum = Math.min(...data.matches.map((match) => Number(match.start_time)));
    state.maximum = Math.max(
      ...data.matches.map((match) => Number(match.start_time) + Number(match.duration || 0)),
    );
    state.from = state.minimum;
    state.to = state.maximum;

    for (const range of [elements.fromRange, elements.toRange]) {
      range.min = String(state.minimum);
      range.max = String(state.maximum);
      range.step = "60";
    }
    for (const input of [elements.fromDate, elements.toDate]) {
      input.min = toDateInput(state.minimum);
      input.max = toDateInput(state.maximum);
    }

    elements.heroStatus.textContent = `${data.matches.length} 场战报已入册 · 联赛 ${data.league_id}`;
    elements.updateStamp.textContent = `数据更新时间：${formatJst(Date.parse(data.generated_at_utc) / 1000)} JST`;
    elements.minimumTime.textContent = formatShortJst(state.minimum);
    elements.maximumTime.textContent = formatShortJst(state.maximum);
    elements.availableCount.textContent = String(data.metadata?.matches_available ?? data.matches.length);
    showCoverageNotice(data);
    bindControls();
    const allButton = elements.quickButtons.find((button) => button.dataset.range === "all");
    setRange(state.minimum, state.maximum, allButton);
  } catch (error) {
    elements.heroStatus.textContent = "战报暂时未能展开";
    elements.updateStamp.textContent = "数据读取失败";
    elements.coverageNotice.textContent = `暂时无法读取联赛数据：${error.message}`;
    elements.coverageNotice.hidden = false;
    elements.statsEmpty.hidden = false;
    elements.matrixEmpty.hidden = false;
    elements.matrixScroll.hidden = true;
  }
}

load();
