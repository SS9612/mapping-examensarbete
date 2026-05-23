import client from "./client";

export async function mapSingle(competence) {
  const res = await client.post("/api/area-mapper/map", { competence }, { timeout: 120000 });
  return res.data;
}

/** Start async map-lines job. Returns { jobId }. Backend processes in queue. */
export async function mapLinesStart(text) {
  const res = await client.post("/api/area-mapper/map-lines", text, {
    headers: { "Content-Type": "text/plain" },
  });
  return res.data;
}

/** Get status of a map-lines job. Status has results, errors, isCompleted, processed, total, etc. */
export async function getMapLinesStatus(jobId) {
  const res = await client.get(`/api/area-mapper/map-lines/${jobId}`);
  return res.data;
}