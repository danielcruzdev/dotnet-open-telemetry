---
name: semantic-commits
description: Conventional Commits convention for this repository — types, scopes, subject and body rules, and how to split a batch of changes into coherent commits. Use whenever committing, writing a commit message, deciding how many commits a change should become, or reviewing commit history.
---

# Semantic Commits

Format:

```
<tipo>(<escopo>): <assunto>

<corpo — o porquê, não o quê>

<rodapé>
```

**Commit messages are written in Portuguese** (the working language of this repo). The types and scopes below stay in English — they are identifiers, not prose.

## Types

| Type | Use |
|---|---|
| `feat` | New behavior visible to a user of the system |
| `fix` | Corrects broken behavior |
| `refactor` | Restructures without changing behavior |
| `test` | Adds or changes tests only |
| `docs` | Documentation only — README, `.specs/`, `.claude/` |
| `build` | Projects, packages, MSBuild, `Directory.*.props` |
| `chore` | Housekeeping that fits nothing above (`.gitignore`, scripts) |
| `perf` | Performance change |

If a commit needs two types, it is two commits.

## Scopes

Use the area actually touched:

`observability` (the correlation contract, `Shared.Observability`) · `servicedefaults` · `bff` · `core` · `proxy` · `apphost` · `specs` (`.specs/`) · `skills` (`.claude/`) · `deps`

Omit the scope when a change is genuinely repo-wide. Never invent a scope for a single file.

## Subject line

- Imperative, present tense: `adiciona`, `corrige`, `remove` — not `adicionado` or `adicionando`
- Lowercase after the colon, no trailing period
- Under 72 characters
- Says what changed, not which files: `corrige a marcação do span raiz`, not `atualiza CorrelationIdMiddleware.cs`

## Body

Required whenever the reason is not obvious from the subject — which is most of the time here.

Explain **why**, and what breaks without it. The diff already shows what changed; the body exists for the reader who is asking "why was this done this way?" six months from now.

Worth including when true:
- The failure mode the change prevents, especially a silent one
- A decision and the alternative rejected
- A discovery that contradicts documentation or intuition
- Known limits of the change

Wrap at 72 characters. Bullets with `-` are fine.

<Good>
```
fix(observability): marca o span raiz no middleware

O CorrelationIdSpanProcessor nao alcanca o span do servidor: ele nasce
antes de qualquer middleware, quando o Baggage ainda esta vazio. O
resultado e todo span filho correto e o raiz sem o correlation.id, o
que passa despercebido porque nada lanca erro.

Coberto por Span_raiz_do_servidor_carrega_o_correlation_id.
```
</Good>

<Bad>
```
fix: ajustes no middleware

Atualiza o CorrelationIdMiddleware.cs e corrige alguns pontos.
```
Says nothing the diff doesn't. No reason, no failure mode.
</Bad>

## Breaking changes

`!` after the scope, and a `BREAKING CHANGE:` footer explaining the migration:

```
feat(observability)!: renomeia o header para X-Correlation-Id

BREAKING CHANGE: servicos que enviavam X-Request-Id deixam de ser
correlacionados ate atualizarem o header.
```

In this project, changing any name in the correlation contract — header, baggage key, span attribute, log property — is always breaking, even when nothing fails to compile.

## Splitting a batch

One commit = one coherent reason to change. Split when the parts could be reviewed, reverted, or explained independently.

A useful split for a phase of `.specs/PROGRESSO.md`:

1. `feat` — the library or behavior, with the tests that prove it
2. `feat` — wiring it into the services
3. `docs(skills)` — documentation the implementation contradicted
4. `docs(specs)` — PROGRESSO updated with the evidence

Keep implementation and its tests together when built with TDD: they are one reason to change, and separating them produces a commit whose claim is unproven.

Do **not** split by file type or by directory. `src/` in one commit and `tests/` in another is not a semantic split.

Every commit should build and pass tests on its own. If you cannot verify that for each one, say so rather than implying a bisectable history.

## Footer

End every commit with:

```
Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: <session url>
```

No emoji, no `🤖 Generated with`, no marketing line in commit bodies.

## Checklist

1. Type matches the actual nature of the change.
2. Scope is a real area, or absent.
3. Subject imperative, lowercase, no period, under 72 chars.
4. Body explains why — and names the silent failure mode when there is one.
5. Contradicted documentation updated in its own `docs` commit.
6. Nothing unrelated swept in: check `git status` before staging.
7. Build and tests verified — state what you actually ran.
