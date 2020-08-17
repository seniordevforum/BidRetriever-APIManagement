﻿using System;
using System.Collections.Generic;
using _440DocumentManagement.Helpers;
using _440DocumentManagement.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace _440DocumentManagement.Controllers
{
	[Produces("application/json")]
	[Route("api")]
	public class PublishedDocumentManagementController : Controller
	{
		private readonly DatabaseHelper _dbHelper;

		public PublishedDocumentManagementController()
		{
			_dbHelper = new DatabaseHelper();
		}

		[HttpPost]
		[Route("CreatePublishedDocument")]
		public IActionResult Post(PublishedDocument publishedDocument)
		{
			try
			{
				// check required parameters
				var missingParameter = publishedDocument.CheckRequiredParameters(new string[]
				{
					"doc_id", "file_id", "project_id", "customer_id", "customer_name", "destination_name",
					"destination_url", "destination_username", "destination_folder_path", "destination_file_name",
					"publish_datetime", "publish_status", "destination_sys_type_id", "file_original_filename",
					"bucket_name"
				});

				if (missingParameter != null)
				{
					return BadRequest(new
					{
						status = $"{missingParameter} is required"
					});
				}

				// write log record
				var docPublishId = publishedDocument.doc_publish_id ?? Guid.NewGuid().ToString();
				var destinationFileSize = StringHelper.ConvertToInteger(publishedDocument.destination_file_size);
				var destinationTransferTime = StringHelper.ConvertToInteger(publishedDocument.destination_transfer_time);

				using (var cmd = _dbHelper.SpawnCommand())
				{
					var columns = "(doc_publish_id, doc_id, file_id, project_id, customer_id, customer_name, project_name, "
						+ "submitter_id, submission_id, submission_datetime, destination_id, destination_name, destination_url, "
						+ "destination_username, destination_folder_path, destination_file_name, publish_datetime, publish_status, destination_file_size, "
						+ "destination_transfer_time, destination_sys_type_id, destination_sys_type_name, file_original_filename, "
						+ "doc_name, doc_number, doc_revision, bucket_name, doc_discipline, status, create_datetime, edit_datetime)";
					var values = "(@doc_publish_id, @doc_id, @file_id, @project_id, @customer_id, @customer_name, @project_name, "
						+ "@submitter_id, @submission_id, @submission_datetime, @destination_id, @destination_name, @destination_url, "
						+ "@destination_username, @destination_folder_path, @destination_file_name, @publish_datetime, @publish_status, @destination_file_size, "
						+ "@destination_transfer_time, @destination_sys_type_id, @destination_sys_type_name, @file_original_filename, "
						+ "@doc_name, @doc_number, @doc_revision, @bucket_name, @doc_discipline, @status, @create_datetime, @edit_datetime)";

					cmd.CommandText = "INSERT INTO project_documents_published " + columns + " VALUES" + values;

					cmd.Parameters.AddWithValue("doc_publish_id", docPublishId);
					cmd.Parameters.AddWithValue("doc_id", publishedDocument.doc_id);
					cmd.Parameters.AddWithValue("file_id", publishedDocument.file_id);
					cmd.Parameters.AddWithValue("project_id", publishedDocument.project_id);
					cmd.Parameters.AddWithValue("customer_id", publishedDocument.customer_id);
					cmd.Parameters.AddWithValue("customer_name", publishedDocument.customer_name);
					cmd.Parameters.AddWithValue("project_name", publishedDocument.project_name ?? "");
					cmd.Parameters.AddWithValue("submitter_id", publishedDocument.submitter_id ?? "");
					cmd.Parameters.AddWithValue("submission_id", publishedDocument.submission_id ?? "");
					cmd.Parameters.AddWithValue(
							"submission_datetime",
							DateTimeHelper.ConvertToUTCDateTime(publishedDocument.submission_datetime));
					cmd.Parameters.AddWithValue("destination_id", publishedDocument.destination_id ?? "");
					cmd.Parameters.AddWithValue("destination_name", publishedDocument.destination_name);
					cmd.Parameters.AddWithValue("destination_url", publishedDocument.destination_url);
					cmd.Parameters.AddWithValue("destination_username", publishedDocument.destination_username);
					cmd.Parameters.AddWithValue("destination_folder_path", publishedDocument.destination_folder_path);
					cmd.Parameters.AddWithValue("destination_file_name", publishedDocument.destination_file_name);
					cmd.Parameters.AddWithValue(
							"publish_datetime",
							DateTimeHelper.ConvertToUTCDateTime(publishedDocument.publish_datetime));
					cmd.Parameters.AddWithValue("publish_status", publishedDocument.publish_status);
					cmd.Parameters.AddWithValue(
							"destination_file_size",
							destinationFileSize > 0 ? (object)destinationFileSize : DBNull.Value);
					cmd.Parameters.AddWithValue(
							"destination_transfer_time",
							destinationTransferTime > 0 ? (object)destinationTransferTime : DBNull.Value);
					cmd.Parameters.AddWithValue("destination_sys_type_id", publishedDocument.destination_sys_type_id);
					cmd.Parameters.AddWithValue(
							"destination_sys_type_name",
							publishedDocument.destination_sys_type_name ?? "");
					cmd.Parameters.AddWithValue("file_original_filename", publishedDocument.file_original_filename);
					cmd.Parameters.AddWithValue("doc_name", publishedDocument.doc_name ?? "");
					cmd.Parameters.AddWithValue("doc_number", publishedDocument.doc_number ?? "");
					cmd.Parameters.AddWithValue("doc_revision", publishedDocument.doc_revision ?? "");
					cmd.Parameters.AddWithValue("bucket_name", publishedDocument.bucket_name);
					cmd.Parameters.AddWithValue("doc_discipline", publishedDocument.doc_discipline ?? "");
					cmd.Parameters.AddWithValue("status", publishedDocument.status ?? "active");
					cmd.Parameters.AddWithValue("create_datetime", DateTime.UtcNow);
					cmd.Parameters.AddWithValue("edit_datetime", DateTime.UtcNow);

					cmd.ExecuteNonQuery();
				}

				return Ok(new
				{
					doc_publish_id = docPublishId,
					status = "completed"
				});
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}


		[HttpGet]
		[Route("FindPublishedDocuments")]
		public IActionResult Get(PublishedDocumentGetRequest request)
		{
			try
			{
				if (request.project_id == null
						&& request.doc_id == null
						&& request.customer_id == null
						&& request.submission_id == null
						&& request.file_id == null)
				{
					return BadRequest(new
					{
						status = "provide query parameters"
					});
				}

				var detailLevel = request.detail_level ?? "basic";

				using (var cmd = _dbHelper.SpawnCommand())
				{
					var whereString = " WHERE ";

					if (request.project_id != null)
					{
						whereString = whereString + "project_id='" + request.project_id + "' AND ";
					}
					if (request.doc_id != null)
					{
						whereString = whereString + "doc_id='" + request.doc_id + "' AND ";
					}
					if (request.submission_id != null)
					{
						whereString = whereString + "submission_id='" + request.submission_id + "' AND ";
					}
					if (request.file_id != null)
					{
						whereString = whereString + "file_id='" + request.file_id + "' AND ";
					}
					if (request.customer_id != null)
					{
						whereString = whereString + "customer_id='" + request.customer_id + "' AND ";
					}

					whereString = whereString.Remove(whereString.Length - 5);

					cmd.CommandText = "SELECT doc_publish_id, project_id, customer_id, submission_id, "
						+ "doc_id, file_id, customer_name, status, destination_folder_path, "
						+ "destination_id, publish_datetime, destination_file_name, destination_sys_type_id, "
						+ "doc_number, doc_revision, "
						+ "destination_id, destination_url, destination_username, "
						+ "publish_status, destination_file_size, destination_transfer_time, "
						+ "bucket_name, create_datetime, status, edit_datetime, "
						+ "create_user_id, edit_user_id, "
						+ "project_name, submitter_id "
						+ "FROM project_documents_published" + whereString;

					using (var reader = cmd.ExecuteReader())
					{
						var resultList = new List<Dictionary<string, string>>();

						while (reader.Read())
						{
							var result = new Dictionary<string, string>
							{
								["doc_publish_id"] = _dbHelper.SafeGetString(reader, 0),
								["project_id"] = _dbHelper.SafeGetString(reader, 1),
								["customer_id"] = _dbHelper.SafeGetString(reader, 2),
								["submission_id"] = _dbHelper.SafeGetString(reader, 3),
								["doc_id"] = _dbHelper.SafeGetString(reader, 4),
								["file_id"] = _dbHelper.SafeGetString(reader, 5),
								["customer_name"] = _dbHelper.SafeGetString(reader, 6),
								["status"] = _dbHelper.SafeGetString(reader, 7),
								["destination_folder_path"] = _dbHelper.SafeGetString(reader, 8),
								["destination_id"] = _dbHelper.SafeGetString(reader, 9),
								["publish_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 10),
								["destination_file_name"] = _dbHelper.SafeGetString(reader, 11),
								["destination_sys_type_id"] = _dbHelper.SafeGetString(reader, 12),
								["doc_number"] = _dbHelper.SafeGetString(reader, 13),
								["doc_revision"] = _dbHelper.SafeGetString(reader, 14),
								["project_name"] = _dbHelper.SafeGetString(reader, 27),
								["submitter_id"] = _dbHelper.SafeGetString(reader, 28),
							};

							if (detailLevel == "all" || detailLevel == "admin")
							{
								result["destination_id"] = _dbHelper.SafeGetString(reader, 15);
								result["destination_url"] = _dbHelper.SafeGetString(reader, 16);
								result["destination_username"] = _dbHelper.SafeGetString(reader, 17);
								result["publish_status"] = _dbHelper.SafeGetString(reader, 18);
								result["destination_file_size"] = _dbHelper.SafeGetInteger(reader, 19);
								result["destination_transfer_time"] = _dbHelper.SafeGetInteger(reader, 20);
								result["bucket_name"] = _dbHelper.SafeGetString(reader, 21);
								result["create_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 22);
								result["status"] = _dbHelper.SafeGetString(reader, 23);
								result["edit_datetime"] = _dbHelper.SafeGetDatetimeString(reader, 24);
							}

							if (detailLevel == "admin")
							{
								result["create_user_id"] = _dbHelper.SafeGetString(reader, 25);
								result["edit_user_id"] = _dbHelper.SafeGetString(reader, 26);
							}

							resultList.Add(result);
						}
						return Ok(resultList);
					}
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}


		[HttpPost]
		[Route("UpdatePublishedDocument")]
		public IActionResult Post(PublishedDocumentUpdateRequest request)
		{
			try
			{
				// validation check
				if (request.search_doc_publish_id == null)
				{
					return BadRequest(new { status = "please provide search_publish_id" });
				}

				using (var cmd = _dbHelper.SpawnCommand())
				{
					var destinationFileSize = StringHelper.ConvertToInteger(request.destination_file_size);
					var destinationTransferTime = StringHelper.ConvertToInteger(request.destination_transfer_time);

					var whereString = " WHERE doc_publish_id='" + request.search_doc_publish_id + "'";

					var queryString = "UPDATE project_documents_published SET "
						+ "customer_id = COALESCE(@customer_id, customer_id), "
						+ "customer_name = COALESCE(@customer_name, customer_name), "
						+ "destination_file_name = COALESCE(@destination_file_name, destination_file_name), "
						+ "destination_file_size = COALESCE(@destination_file_size, destination_file_size), "
						+ "destination_folder_path = COALESCE(@destination_folder_path, destination_folder_path), "
						+ "destination_id = COALESCE(@destination_id, destination_id), "
						+ "destination_name = COALESCE(@destination_name, destination_name), "
						+ "destination_sys_type_id = COALESCE(@destination_sys_type_id, destination_sys_type_id), "
						+ "destination_sys_type_name = COALESCE(@destination_sys_type_name, destination_sys_type_name), "
						+ "destination_transfer_time = COALESCE(@destination_transfer_time, destination_transfer_time), "
						+ "destination_url = COALESCE(@destination_url, destination_url), "
						+ "destination_username = COALESCE(@destination_username, destination_username), "
						+ "doc_discipline = COALESCE(@doc_discipline, doc_discipline), "
						+ "doc_id = COALESCE(@doc_id, doc_id), "
						+ "doc_name = COALESCE(@doc_name, doc_name), "
						+ "doc_number = COALESCE(@doc_number, doc_number), "
						+ "doc_revision = COALESCE(@doc_revision, doc_revision), "
						+ "create_user_id = COALESCE(@create_user_id, create_user_id), "
						+ "edit_user_id = COALESCE(@edit_user_id, edit_user_id), "
						+ "file_id = COALESCE(@file_id, file_id), "
						+ "file_original_filename = COALESCE(@file_original_filename, file_original_filename), "
						+ "project_id = COALESCE(@project_id, project_id), "
						+ "project_name = COALESCE(@project_name, project_name), "
						+ "publish_datetime = COALESCE(@publish_datetime, publish_datetime), "
						+ "publish_status = COALESCE(@publish_status, publish_status), "
						+ "submission_datetime = COALESCE(@submission_datetime, submission_datetime), "
						+ "submission_id = COALESCE(@submission_id, submission_id), "
						+ "submitter_id = COALESCE(@submitter_id, submitter_id), "
						+ "status = COALESCE(@status, status), edit_datetime = @edit_datetime" + whereString;

					cmd.CommandText = queryString;

					cmd.Parameters.AddWithValue("customer_id", (object)request.customer_id ?? DBNull.Value);
					cmd.Parameters.AddWithValue("customer_name", (object)request.customer_name ?? DBNull.Value);
					cmd.Parameters.AddWithValue("destination_file_name", (object)request.destination_file_name ?? DBNull.Value);
					cmd.Parameters.AddWithValue("destination_file_size", destinationFileSize > 0 ? (object)destinationFileSize : DBNull.Value);
					cmd.Parameters.AddWithValue("destination_folder_path", (object)request.destination_folder_path ?? DBNull.Value);
					cmd.Parameters.AddWithValue("destination_id", (object)request.destination_id ?? DBNull.Value);
					cmd.Parameters.AddWithValue("destination_name", (object)request.destination_name ?? DBNull.Value);
					cmd.Parameters.AddWithValue("destination_sys_type_id", (object)request.destination_sys_type_id ?? DBNull.Value);
					cmd.Parameters.AddWithValue("destination_sys_type_name", (object)request.destination_sys_type_name ?? DBNull.Value);
					cmd.Parameters.AddWithValue("destination_transfer_time", destinationTransferTime > 0 ? (object)destinationTransferTime : DBNull.Value);
					cmd.Parameters.AddWithValue("destination_url", (object)request.destination_url ?? DBNull.Value);
					cmd.Parameters.AddWithValue("destination_username", (object)request.destination_username ?? DBNull.Value);
					cmd.Parameters.AddWithValue("doc_discipline", (object)request.doc_discipline ?? DBNull.Value);
					cmd.Parameters.AddWithValue("doc_id", (object)request.doc_id ?? DBNull.Value);
					cmd.Parameters.AddWithValue("doc_name", (object)request.doc_name ?? DBNull.Value);
					cmd.Parameters.AddWithValue("doc_number", (object)request.doc_number ?? DBNull.Value);
					cmd.Parameters.AddWithValue("doc_revision", (object)request.doc_revision ?? DBNull.Value);
					cmd.Parameters.AddWithValue("create_user_id", (object)request.create_user_id ?? DBNull.Value);
					cmd.Parameters.AddWithValue("edit_user_id", (object)request.edit_user_id ?? DBNull.Value);
					cmd.Parameters.AddWithValue("file_id", (object)request.file_id ?? DBNull.Value);
					cmd.Parameters.AddWithValue("file_original_filename", (object)request.file_original_filename ?? DBNull.Value);
					cmd.Parameters.AddWithValue("project_id", (object)request.project_id ?? DBNull.Value);
					cmd.Parameters.AddWithValue("project_name", (object)request.project_name ?? DBNull.Value);
					cmd.Parameters.AddWithValue("publish_datetime", request.publish_datetime != null ? (object)(DateTimeHelper.ConvertToUTCDateTime(request.publish_datetime)) : DBNull.Value);
					cmd.Parameters.AddWithValue("publish_status", (object)request.publish_status ?? DBNull.Value);
					cmd.Parameters.AddWithValue("status", (object)request.status ?? DBNull.Value);
					cmd.Parameters.AddWithValue("submission_datetime", request.submission_datetime != null ? (object)(DateTimeHelper.ConvertToUTCDateTime(request.submission_datetime)) : DBNull.Value);
					cmd.Parameters.AddWithValue("submission_id", (object)request.submission_id ?? DBNull.Value);
					cmd.Parameters.AddWithValue("submitter_id", (object)request.submitter_id ?? DBNull.Value);
					cmd.Parameters.AddWithValue("edit_datetime", DateTime.UtcNow);

					if (cmd.ExecuteNonQuery() == 0)
					{
						return BadRequest(new
						{
							status = "no matching published document found"
						});
					}
					else
					{
						return Ok(new
						{
							status = "completed"
						});
					}
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}


		[HttpPost]
		[Route("DeletePublishedDocuments")]
		public IActionResult Post(PublishedDocumentDeleteRequest request)
		{
			try
			{
				if (string.IsNullOrEmpty(request.submission_id))
				{
					return BadRequest(new
					{
						status = "Please provide submission_id"
					});
				}

				using (var cmd = _dbHelper.SpawnCommand())
				{
					cmd.CommandText = "DELETE FROM project_documents_published WHERE submission_id='" + request.submission_id + "'";
					var deletedCount = cmd.ExecuteNonQuery();

					return Ok(new
					{
						status = $"Deleted {deletedCount} record(s)"
					});
				}
			}
			catch (Exception exception)
			{
				return BadRequest(new
				{
					status = exception.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}

		[HttpGet]
		[Route("GetDailyDocumentDigest")]
		public IActionResult GetDailyDocumentDigest(GetDailyDocumentDigestRequestModel request)
		{
			try
			{
				if (request == null)
				{
					return BadRequest(new
					{
						status = "error",
						message = "Request can't be null"
					});
				}

				var missingParameter = request.CheckRequiredParameters(new string[]
				{
					"DailyDigestDate"
				});
				if (missingParameter != null)
				{
					return BadRequest(new
					{
						status = "error",
						message = $"{missingParameter} is required"
					});
				}

				using (var cmd = _dbHelper.SpawnCommand())
				{
					var startDate = new DateTime(request.DailyDigestDate.Value.Year, request.DailyDigestDate.Value.Month, request.DailyDigestDate.Value.Day, 10, 0, 0, DateTimeKind.Utc);
					cmd.CommandText = 
						"SELECT "
							+ "customers.customer_id as customer_id, "
							+ "customers.customer_name as customer_name, "
							+ "count( DISTINCT project_documents.doc_id ) AS num_docs_changed, "
							+ "projects.project_bid_datetime as project_bid_datetime, "
							+ "users.user_displayname AS project_admin_user_displayname, "
							+ "users.user_email AS project_admin_user_email, "
							+ "folder_transaction_log.project_id as project_id, "
							+ "projects.project_name as project_name, "
							+ "projects.project_assigned_office_name as project_office_name "
						+ "FROM folder_transaction_log "
						+ "LEFT JOIN projects ON folder_transaction_log.project_id = projects.project_id "
						+ "LEFT JOIN project_documents ON folder_transaction_log.doc_id = project_documents.doc_id "
						+ "LEFT JOIN customers ON projects.project_customer_id = customers.customer_id "
						+ "LEFT JOIN users ON projects.project_admin_user_id = users.user_id "
						+ "WHERE "
							+ "project_documents.create_datetime >= @start_date "
							+ "AND project_documents.create_datetime <= @start_date + interval '1 day' "
							+ "AND projects.auto_update_status = 'active' "
						+ "GROUP BY "
							+ "customers.customer_id, customers.customer_name, projects.project_name, folder_transaction_log.project_id, "
							+ "projects.project_admin_user_id, projects.project_assigned_office_name, projects.project_bid_datetime, "
							+ "users.user_displayname, users.user_email "
						+ "ORDER BY "
							+ "customers.customer_name ASC, projects.project_assigned_office_name ASC, projects.project_name ASC ";

					cmd.Parameters.AddWithValue("@start_date", startDate);

					var resultList = new List<Dictionary<string, object>>();
					using (var reader = cmd.ExecuteReader())
					{
						if (reader.HasRows)
						{
							while (reader.Read())
							{
								resultList.Add(new Dictionary<string, object>()
								{
									{ "customer_id", _dbHelper.SafeGetString(reader, 0) },
									{ "customer_name", _dbHelper.SafeGetString(reader, 1) },
									{ "num_docs_changed", _dbHelper.SafeGetIntegerRaw(reader, 2) },
									{ "project_bid_datetime", _dbHelper.SafeGetDatetimeString(reader, 3) },
									{ "project_admin_user_displayname", _dbHelper.SafeGetString(reader, 4) },
									{ "project_admin_user_email", _dbHelper.SafeGetString(reader, 5) },
									{ "project_id", _dbHelper.SafeGetString(reader, 6) },
									{ "project_name", _dbHelper.SafeGetString(reader, 7) },
									{ "project_office_name", _dbHelper.SafeGetString(reader, 8) }
								});
							}
						}
					}

					return Ok(resultList);
				}
			}
			catch (Exception ex)
			{
				return BadRequest(new
				{
					status = "error",
					message = ex.Message
				});
			}
			finally
			{
				_dbHelper.CloseConnection();
			}
		}
	}
}