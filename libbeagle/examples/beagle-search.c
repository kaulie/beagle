#include <stdlib.h>
#include <glib.h>
#include <string.h>
#include <beagle/beagle.h>

static int total_hits;

static void
print_feed_item_hit (BeagleHit *hit)
{
	const gchar *text;
	
	text = beagle_hit_get_property (hit, "dc:title");
	g_print ("Blog: %s\n", text);
}

static void
print_file_hit (BeagleHit *hit)
{
	g_print ("File: %s\n", beagle_hit_get_uri (hit));
}

static void
print_other_hit (BeagleHit *hit)
{
	g_print ("%s (%s)", beagle_hit_get_uri (hit),
		 beagle_hit_get_source_object_name (hit));
}

static void
print_hit (BeagleHit *hit) 
{
	if (strcmp (beagle_hit_get_type (hit), "FeedItem") == 0) {
		print_feed_item_hit (hit);
	} 
	else if (strcmp (beagle_hit_get_type (hit), "File") == 0) {
		print_file_hit (hit);
	} else {
		print_other_hit (hit);
	}
}

static void
hits_added_cb (BeagleQuery *query, BeagleHitsAddedResponse *response) 
{
	GSList *hits, *l;
	gint    i;
	gint    nr_hits;

	hits = beagle_hits_added_response_get_hits (response);

	nr_hits = g_slist_length (hits);
	total_hits += nr_hits;
	g_print ("Found hits (%d):\n", nr_hits);
	g_print ("-------------------------------------------\n");
	for (l = hits, i = 1; l; l = l->next, ++i) {
		g_print ("[%d] ", i);

		print_hit (BEAGLE_HIT (l->data));

		g_print ("\n");
	}
	g_print ("-------------------------------------------\n\n\n");
}

static void
finished_cb (BeagleQuery            *query,
	     BeagleFinishedResponse *response, 
	     GMainLoop              *main_loop)
{
	g_main_loop_quit (main_loop);
}

int
main (int argc, char **argv)
{
	BeagleClient   *client;
	BeagleQuery    *query;
	GMainLoop      *main_loop;
	gint            i;
	
	if (argc < 2) {
		g_print ("Usage %s \"query string\"\n", argv[0]);
		exit (1);
	}
	
	g_type_init ();

	total_hits = 0;

	client = beagle_client_new (NULL);

	main_loop = g_main_loop_new (NULL, FALSE);

	query = beagle_query_new ();

	for (i = 1; i < argc; ++i) {
		beagle_query_add_text (query, argv[i]);
	}

	g_signal_connect (query, "hits-added",
			  G_CALLBACK (hits_added_cb),
			  client);

	g_signal_connect (query, "finished",
			  G_CALLBACK (finished_cb),
			  main_loop);
	
	beagle_client_send_request_async (client, BEAGLE_REQUEST (query),
					  NULL);

	g_main_loop_run (main_loop);
	g_object_unref (query);
	g_object_unref (client);
	g_main_loop_unref (main_loop);

	g_print ("Found a total of %d hits\n", total_hits);
	
	return 0;
}