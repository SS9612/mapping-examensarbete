import client from "./client";

// GET /api/review/pending?skip=&take=
export async function getPending(skip = 0, take = 50) {
  const res = await client.get("/api/review/pending", {
    params: { skip, take },
  });
  return res.data;
}

export async function getApproved(skip = 0, take = 50) {
  const res = await client.get("/api/review/approved", { params: { skip, take } });
  return res.data;
}

export async function getRejected(skip = 0, take = 50) {
  const res = await client.get("/api/review/rejected", {
    params: { skip, take },
  });
  return res.data;
}

export async function getLegacy(skip = 0, take = 50) {
  const res = await client.get("/api/review/legacy", {
    params: { skip, take },
  });
  return res.data;
}

export async function getImported(skip = 0, take = 50) {
  const res = await client.get("/api/review/imported", {
    params: { skip, take },
  });
  return res.data;
}

// GET /api/review/archive?skip=&take=
// Combined archive view (LegacyImported + ImportedCompleted)
export async function getArchive(skip = 0, take = 50) {
  const res = await client.get("/api/review/archive", {
    params: { skip, take },
  });
  return res.data;
}

// GET /api/review/counts
// Lightweight aggregate counts for dashboard KPIs
export async function getCounts() {
  const res = await client.get("/api/review/counts");
  return res.data;
}

// Get initial batch of competences (1000 items) for fast initial load
// This replaces getAllPending/Approved/Rejected for better performance
export async function getInitialBatch(statusType) {
  const take = 1000;
  if (statusType === "pending") return getPending(0, take);
  if (statusType === "approved") return getApproved(0, take);
  if (statusType === "rejected") return getRejected(0, take);
  if (statusType === "legacy") return getLegacy(0, take);
  if (statusType === "imported") return getImported(0, take);
  if (statusType === "archive") return getArchive(0, take);
  return [];
}

export async function getAllApproved() {
  const allCompetences = [];
  let skip = 0;
  const take = 1000; 
  let hasMore = true;

  while (hasMore) {
    const batch = await getApproved(skip, take);
    if (batch && batch.length > 0) {
      allCompetences.push(...batch);
      skip += batch.length;
      hasMore = batch.length === take;
    } else {
      hasMore = false;
    }
  }

  return allCompetences;
}

// GET /api/review/{id}
export async function getCompetence(id) {
  const res = await client.get(`/api/review/${id}`);
  return res.data;
}

// POST /api/review/{id}/approve
export async function approveCompetence(id, reviewNotes) {
  const res = await client.post(`/api/review/${id}/approve`, {
    reviewNotes,
  });
  return res.data;
}

// POST /api/review/{id}/reject
export async function rejectCompetence(id, reviewNotes) {
  const res = await client.post(`/api/review/${id}/reject`, {
    reviewNotes,
  });
  return res.data;
}

// POST /api/review/reject/bulk
export async function bulkReject(competenceIds, reviewNotes) {
  const res = await client.post("/api/review/reject/bulk", {
    competenceIds,
    reviewNotes,
  });
  return res.data;
}

// POST /api/review/{id}/assign-other
export async function assignToOther(id, reviewNotes) {
  const res = await client.post(`/api/review/${id}/assign-other`, {
    reviewNotes,
  });
  return res.data;
}

// GET /api/review/metadata
export async function getMetadata() {
  const res = await client.get("/api/review/metadata");
  return res.data;
}

// PATCH /api/review/{id}/update-categorization (optional name: updates competence name with duplicate check vs legacy)
export async function updateCategorization(competenceId, areaId, categoryId, subcategoryId, name) {
  const body = {
    areaId,
    categoryId: categoryId || null,
    subcategoryId: subcategoryId || null,
  };
  if (name != null && name.trim() !== "") {
    body.name = name.trim();
  }
  const res = await client.patch(`/api/review/${competenceId}/update-categorization`, body);
  return res.data;
}

// POST /api/review/approve/bulk
export async function bulkApprove(competenceIds, reviewNotes) {
  const res = await client.post("/api/review/approve/bulk", {
    competenceIds,
    reviewNotes,
  });
  return res.data;
}

// POST /api/review/imported/bulk
export async function bulkMarkImported(competenceIds) {
  const res = await client.post("/api/review/imported/bulk", {
    competenceIds,
  });
  return res.data;
}

// POST /api/review/pending/bulk
export async function bulkMoveToPending(competenceIds) {
  const res = await client.post("/api/review/pending/bulk", {
    competenceIds,
  });
  return res.data;
}

// POST /api/review/delete/bulk
export async function bulkDelete(competenceIds) {
  const res = await client.post("/api/review/delete/bulk", {
    competenceIds,
  });
  return res.data;
}

// DELETE /api/review/{id}
export async function deleteCompetence(id) {
  const res = await client.delete(`/api/review/${id}`);
  return res.data;
}