export interface Session {
  id: string;
  code: string;
  provider: string;
  specialty: string;
  location: string;
  startsAt: string;
  capacitySlots: number;
  remainingSlots: number;
}

export type AppointmentStatus = 'Requested' | 'Held' | 'Confirmed' | 'Cancelled' | 'Rejected';

export interface Appointment {
  id: string;
  sessionCode: string;
  reference: string;
  slots: number;
  feeCents: number;
  status: AppointmentStatus;
  reason: string | null;
  letterNumber: string | null;
  createdAt: string;
  updatedAt: string;
  slotsReleasedAt: string | null;
}

export interface LogEntry {
  id: string;
  type: string;
  module: string;
  occurredAt: string;
}
