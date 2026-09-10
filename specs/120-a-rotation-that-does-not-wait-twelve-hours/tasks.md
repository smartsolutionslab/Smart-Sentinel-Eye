# Tasks — spec 120

Tracked at feature granularity against #2161 (CLAUDE.md, Phase 3). No per-task
issues since spec 028.

**Phase 4a colour: RED.** New behaviour — the validator does something it has
never done. Characterisation would have passed quietly and proved nothing.

- [ ] **T1** Verify the seam before planning against either description of it:
      confirm #2099's `internal` metadata constructor exists and that the
      reflection the issue describes is gone.
- [ ] **T2** Confirm `IConfigurationManager<T>` — the interface the seam takes,
      not just the concrete class — declares `RequestRefresh()`.
- [ ] **T3** Write `WhepValidatorRotationTests` over a real
      `ConfigurationManager` and a retriever that rotates its JWKS between
      fetches. Observe the key-rotation case **red**; quote it.
- [ ] **T4** Same for the issuer-rotation case — the field spec 089 added.
- [ ] **T5** Write the negative: a failure a refresh cannot cure (wrong
      audience, expired) must **not** re-fetch discovery. Assert the fetch
      count. Green on arrival, red against "refresh on every failure".
- [ ] **T6** Keep a control: after the rotation the **old** key is refused.
      Rotation means the old key stops working, not that both work.
- [ ] **T7** Add the discriminating `RequestRefresh()` to the
      `SecurityTokenException` arm. Nothing in `CreateParameters` changes.
- [ ] **T8** Confirm every pre-existing Whep test passes **unmodified** —
      issuer, audience, resolution, metadata-source.
- [ ] **T9** `dotnet build -c Release`; run the suite; record counts.
- [ ] **T10** State the §IV reading (not on the path) and the expected #2235
      conflict in `verification.md`.
